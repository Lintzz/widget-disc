using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DiscordVoiceWidget.Rpc;

public enum RpcOpcode
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}

/// <summary>
/// Transporte para o servidor RPC local do Discord.
///
/// Framing: [int32 LE opcode][int32 LE tamanho][JSON UTF-8].
/// Pipe no Windows: \\.\pipe\discord-ipc-{0..9}.
///
/// Esta classe cuida so do transporte: pipe, frames e casamento entre comando e
/// resposta. A logica de voz vive em <see cref="VoiceSessionService"/>, e a
/// politica de reconexao tambem - aqui uma queda vira excecao, nao retentativa.
///
/// O caminho de leitura e o trecho quente do app inteiro (SPEAKING_START/STOP
/// chegam varias vezes por segundo numa call ativa), por isso ele nao aloca por
/// frame: o corpo vem de um ArrayPool e os eventos sao entregues sem copia.
/// </summary>
public sealed class DiscordIpcClient : IAsyncDisposable
{
    private const string ReadyKey = "__READY__";
    private const int HeaderSize = 8;

    /// <summary>Frames reais tem poucos KB; acima disso o fluxo esta corrompido.</summary>
    private const int MaxFrameSize = 16 * 1024 * 1024;

    /// <summary>
    /// Nomes de evento conhecidos, reaproveitados em vez de alocar uma string nova a
    /// cada frame.
    /// </summary>
    private static readonly string[] KnownEvents =
    [
        "SPEAKING_START",
        "SPEAKING_STOP",
        "VOICE_STATE_UPDATE",
        "VOICE_STATE_CREATE",
        "VOICE_STATE_DELETE",
        "VOICE_SETTINGS_UPDATE",
        "VOICE_CHANNEL_SELECT",
        "READY",
        "ERROR",
    ];

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    /// <summary>
    /// Evento vindo do Discord: (nome do evento, payload "data").
    ///
    /// O <see cref="JsonElement"/> so e valido durante a chamada: ele aponta para um
    /// buffer que volta ao pool logo depois. Extraia o que precisar ali mesmo, ou use
    /// <see cref="JsonElement.Clone"/> para guardar.
    /// </summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Todo frame recebido, cru. Mesma regra de validade de <see cref="EventReceived"/>.</summary>
    public event Action<JsonElement>? RawFrameReceived;

    /// <summary>Conexao caiu. Quem escuta decide se reconecta.</summary>
    public event Action<Exception>? Faulted;

    public event Action<string>? Log;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Abre o pipe, faz o HANDSHAKE e devolve o payload do evento READY.</summary>
    public async Task<JsonElement> ConnectAsync(string clientId, CancellationToken ct = default)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // Timeout 0: uma unica tentativa, sem espera. Com timeout positivo o
                // .NET fica tentando em laco ate estourar o prazo - medido: 1s de relogio
                // e ~26ms de CPU por pipe inexistente, vezes 10 pipes, a cada rodada de
                // reconexao enquanto o Discord estiver fechado.
                await pipe.ConnectAsync(0, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            _pipe = pipe;
            Log?.Invoke($@"conectado em \\.\pipe\discord-ipc-{i}");
            break;
        }

        if (_pipe is null)
        {
            throw new IOException(
                @"Nenhum pipe \\.\pipe\discord-ipc-0..9 respondeu. O Discord desktop esta aberto?");
        }

        var ready = NewPending(ReadyKey);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);

        await SendAsync(
            RpcOpcode.Handshake,
            w =>
            {
                w.WriteNumber("v", 1);
                w.WriteString("client_id", clientId);
            },
            ct);

        return await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    /// <summary>
    /// Envia um comando e aguarda a resposta correspondente (casada pelo nonce).
    /// </summary>
    /// <param name="writeArgs">Escreve as propriedades do objeto "args", se houver.</param>
    public async Task<JsonElement> CommandAsync(
        string cmd,
        Action<Utf8JsonWriter>? writeArgs = null,
        string? evt = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var nonce = Guid.NewGuid().ToString();
        var tcs = NewPending(nonce);

        await SendAsync(
            RpcOpcode.Frame,
            w =>
            {
                w.WriteString("cmd", cmd);
                w.WriteString("nonce", nonce);

                if (writeArgs is not null)
                {
                    w.WriteStartObject("args");
                    writeArgs(w);
                    w.WriteEndObject();
                }

                if (evt is not null) w.WriteString("evt", evt);
            },
            ct);

        try
        {
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(nonce, out _);
            throw new TimeoutException($"O Discord nao respondeu ao comando {cmd}.");
        }
    }

    public Task<JsonElement> SubscribeAsync(string evt, Action<Utf8JsonWriter>? writeArgs = null, CancellationToken ct = default)
        => CommandAsync("SUBSCRIBE", writeArgs, evt, ct: ct);

    public Task<JsonElement> UnsubscribeAsync(string evt, Action<Utf8JsonWriter>? writeArgs = null, CancellationToken ct = default)
        => CommandAsync("UNSUBSCRIBE", writeArgs, evt, ct: ct);

    private TaskCompletionSource<JsonElement> NewPending(string key)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        return tcs;
    }

    /// <summary>
    /// Escreve cabecalho e JSON num unico buffer: reserva os 8 bytes do cabecalho,
    /// serializa direto atras deles e so entao preenche o tamanho. Sem serializador
    /// por reflexao e sem copia entre buffers.
    /// </summary>
    private Task SendAsync(RpcOpcode op, Action<Utf8JsonWriter> writeBody, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.GetSpan(HeaderSize);
        buffer.Advance(HeaderSize);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        var frame = MemoryMarshal.AsMemory(buffer.WrittenMemory);
        BinaryPrimitives.WriteInt32LittleEndian(frame.Span[..4], (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.Span[4..HeaderSize], frame.Length - HeaderSize);

        return WriteFrameAsync(frame, ct);
    }

    private Task SendRawAsync(RpcOpcode op, ReadOnlySpan<byte> body, CancellationToken ct)
    {
        var frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame.AsSpan(HeaderSize));
        return WriteFrameAsync(frame, ct);
    }

    private async Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _pipe!.WriteAsync(frame, ct);
            await _pipe.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _pipe!.ReadExactlyAsync(header, ct);
                var op = (RpcOpcode)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

                if (length is < 0 or > MaxFrameSize)
                {
                    throw new InvalidDataException($"Frame com tamanho invalido: {length} bytes.");
                }

                var rented = length > 0 ? ArrayPool<byte>.Shared.Rent(length) : [];
                try
                {
                    var body = rented.AsMemory(0, length);
                    if (length > 0) await _pipe.ReadExactlyAsync(body, ct);
                    HandleFrame(op, body, ct);
                }
                finally
                {
                    if (length > 0) ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // encerramento normal
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[leitura encerrada] {ex.GetType().Name}: {ex.Message}");
            FailAllPending(ex);
            Faulted?.Invoke(ex);
        }
    }

    private void HandleFrame(RpcOpcode op, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        switch (op)
        {
            case RpcOpcode.Ping:
                // O corpo precisa ser copiado: o buffer volta ao pool ao sair daqui.
                _ = SendRawAsync(RpcOpcode.Pong, body.Span, ct);
                return;

            case RpcOpcode.Close:
            {
                var reason = Encoding.UTF8.GetString(body.Span);
                Log?.Invoke($"[CLOSE] {reason}");
                var ex = new IOException($"O Discord fechou a conexao RPC: {reason}");
                FailAllPending(ex);
                Faulted?.Invoke(ex);
                return;
            }

            case RpcOpcode.Frame:
                break;

            default:
                return;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        RawFrameReceived?.Invoke(root);

        var evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? EventNameOf(e) : null;
        var nonce = root.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var data = root.TryGetProperty("data", out var d) ? d : default;

        if (nonce is not null && _pending.TryRemove(nonce, out var pending))
        {
            // Respostas de comando sao aguardadas em outra thread: essas sim precisam
            // de copia propria, que sobrevive ao descarte do documento.
            if (evt == "ERROR") pending.TrySetException(new DiscordRpcException(DescribeError(data)));
            else pending.TrySetResult(data.ValueKind == JsonValueKind.Undefined ? default : data.Clone());
            return;
        }

        if (evt == "READY" && _pending.TryRemove(ReadyKey, out var ready))
        {
            ready.TrySetResult(data.Clone());
            return;
        }

        if (evt == "ERROR")
        {
            Log?.Invoke($"[ERROR sem nonce] {DescribeError(data)}");
            return;
        }

        if (evt is not null) EventReceived?.Invoke(evt, data);
    }

    private static string? EventNameOf(JsonElement element)
    {
        foreach (var known in KnownEvents)
        {
            if (element.ValueEquals(known)) return known;
        }

        return element.GetString();
    }

    private static string DescribeError(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return data.ToString();
        var code = data.TryGetProperty("code", out var c) ? c.ToString() : "?";
        var message = data.TryGetProperty("message", out var m) ? m.GetString() : data.ToString();
        return $"code {code}: {message}";
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();

        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* ja reportado no proprio loop */ }
        }

        if (_pipe is not null) await _pipe.DisposeAsync();
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}

public sealed class DiscordRpcException(string message) : Exception(message);

/// <summary>O endpoint de token recusou client id, client secret ou redirect.</summary>
public sealed class DiscordCredentialsRejectedException(string message) : Exception(message);
