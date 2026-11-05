using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Win32;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>A live do Stream Live: se esta no ar e quem esta assistindo.</summary>
internal sealed record StreamLiveState(bool IsLive, int ViewerCount, IReadOnlyList<string> ViewerNames)
{
    public static readonly StreamLiveState Off = new(false, 0, []);

    // Record compara listas por referencia; aqui interessa o conteudo.
    public bool Equals(StreamLiveState? other) => other is not null
        && IsLive == other.IsLive
        && ViewerCount == other.ViewerCount
        && ViewerNames.SequenceEqual(other.ViewerNames);

    public override int GetHashCode() => HashCode.Combine(IsLive, ViewerCount);
}

/// <summary>
/// Ligacao com o Stream Live (app de transmitir tela entre amigos): "Iniciar live" e
/// "Encerrar live" no menu e a moldura de ao vivo no widget.
///
/// Pelos eventos nomeados que o Stream Live cria (ver WidgetBridge, no repositorio dele),
/// o mesmo meio dos sinais entre instancias deste app. Nada fica consultando: o Stream Live
/// sinaliza "EstadoMudou" a cada inicio/fim de live e a cada amigo que entra ou sai, e o
/// pool de threads acorda so ai. Quem esta assistindo vem de uma memoria compartilhada
/// ("Espectadores"), porque evento nao carrega numero. Para
/// o caso de ele cair no meio da live (sem tempo de avisar), o fim do processo tambem e
/// esperado, e so enquanto a live esta no ar.
///
/// Desligado nas configuracoes, esta classe nao existe: nenhum evento aberto, nenhuma espera.
/// </summary>
internal sealed class StreamLiveLink : IDisposable
{
    private const string Prefix = "StreamLiveApp.";
    private const string ProcessName = "StreamLiveApp";

    /// <summary>AppId do instalador (Inno Setup) do Stream Live; fixo, ver setup.iss dele.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Stream Live_is1";

    private readonly EventWaitHandle _stateChanged;
    private readonly RegisteredWaitHandle _stateWait;
    private readonly object _gate = new();
    /// <summary>Mesmo tamanho que o Stream Live reserva (WidgetBridge.ViewersCapacity).</summary>
    private const int ViewersCapacity = 4096;

    private Process? _watchedProcess;
    private StreamLiveState _state = StreamLiveState.Off;
    private bool _disposed;

    public StreamLiveLink()
    {
        // Cria se o Stream Live ainda nao abriu: o objeto e o mesmo que ele abre depois.
        _stateChanged = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + "EstadoMudou");
        _stateWait = ThreadPool.RegisterWaitForSingleObject(
            _stateChanged, (_, _) => Refresh(), null, Timeout.Infinite, executeOnlyOnce: false);

        // O widget pode abrir com a live ja no ar.
        Refresh();
    }

    /// <summary>A live comecou, acabou ou alguem entrou/saiu. Chega no pool de threads.</summary>
    public event Action<StreamLiveState>? StateChanged;

    public StreamLiveState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>O executavel instalado, ou nulo se o Stream Live nao esta neste PC.</summary>
    public static string? FindExecutable()
    {
        // Instalado "para todos" (o padrao dele, em Arquivos de Programas) ou so para a conta.
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string dir && Executable(dir) is { } found) return found;
            }
            catch (Exception ex)
            {
                FileLog.Write($"Stream Live: registro ilegivel: {ex.Message}");
            }
        }

        return Executable(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Stream Live"));

        static string? Executable(string dir)
        {
            var path = Path.Combine(dir, ProcessName + ".exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Aberto: pede o modal de transmitir a ele. Fechado: abre com --transmitir, que ja
    /// cai no mesmo modal. Retorna false se o Stream Live nao esta instalado.
    /// </summary>
    public static bool StartLive()
    {
        // O Stream Live e quem traz a propria janela para frente; sem esta permissao o
        // Windows so piscaria o botao dele na barra.
        AllowSetForegroundWindow(ASFW_ANY);

        if (Signal("Transmitir")) return true;

        if (FindExecutable() is not { } exe) return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--transmitir",
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
        })?.Dispose();

        return true;
    }

    /// <summary>
    /// Mostra o Stream Live: aberto (inclusive minimizado ou atras do jogo), traz a janela
    /// para a frente; fechado, abre normal. Retorna false se ele nao esta instalado.
    ///
    /// Pela janela, e nao por um sinal: assim funciona com qualquer versao do Stream Live,
    /// sem depender da ponte.
    /// </summary>
    public static bool Open()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            if (processes.Length > 0)
            {
                // Ainda subindo, sem janela: ela aparece na frente sozinha.
                var hwnd = processes.Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
                if (hwnd == IntPtr.Zero) return true;

                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return true;
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        if (FindExecutable() is not { } exe) return false;

        AllowSetForegroundWindow(ASFW_ANY);
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
        })?.Dispose();

        return true;
    }

    /// <summary>Fecha o Stream Live como o X dele, sem a confirmacao. False se ele nao estava aberto.</summary>
    public static bool EndLive() => Signal("Encerrar");

    private static bool Signal(string name)
    {
        // Os eventos de pedido so existem com o Stream Live aberto.
        if (!EventWaitHandle.TryOpenExisting(Prefix + name, out var handle)) return false;

        using (handle)
        {
            handle.Set();
        }
        return true;
    }

    /// <summary>
    /// Le o estado sem segurar o evento: um handle aberto aqui manteria o "AoVivo" vivo
    /// depois de o Stream Live cair.
    /// </summary>
    private static bool ReadLive()
    {
        if (!EventWaitHandle.TryOpenExisting(Prefix + "AoVivo", out var handle)) return false;

        using (handle)
        {
            return handle.WaitOne(0);
        }
    }

    /// <summary>
    /// Quem esta assistindo, na memoria compartilhada que o Stream Live mantem: tamanho em
    /// 4 bytes e depois UTF-8 com a contagem na primeira linha e um nome por linha.
    /// </summary>
    private static (int Count, IReadOnlyList<string> Names) ReadViewers()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(Prefix + "Espectadores", MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, ViewersCapacity, MemoryMappedFileAccess.Read);

            var length = Math.Clamp(view.ReadInt32(0), 0, ViewersCapacity - 4);
            var bytes = new byte[length];
            view.ReadArray(4, bytes, 0, length);
            return ParseViewers(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Stream Live de antes da contagem, ou fechando agora.
            return (0, []);
        }
    }

    internal static (int Count, IReadOnlyList<string> Names) ParseViewers(string text)
    {
        var lines = text.Split('\n');
        return int.TryParse(lines[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? (count, lines.Skip(1).ToArray())
            : (0, []);
    }

    private void Refresh()
    {
        StreamLiveState state;
        bool liveChanged;
        lock (_gate)
        {
            if (_disposed) return;

            var live = ReadLive();
            var (count, names) = live ? ReadViewers() : (0, Array.Empty<string>());
            state = new StreamLiveState(live, count, names);
            if (state == _state) return;

            liveChanged = live != _state.IsLive;
            _state = state;

            if (liveChanged && live) WatchProcess();
            else if (liveChanged) StopWatchingProcess();
        }

        FileLog.Write(liveChanged
            ? state.IsLive ? "Stream Live: ao vivo" : "Stream Live: live encerrada"
            : $"Stream Live: {state.ViewerCount} assistindo");
        StateChanged?.Invoke(state);
    }

    /// <summary>Se o Stream Live cair no meio da live, o fim do processo apaga a moldura.</summary>
    private void WatchProcess()
    {
        StopWatchingProcess();

        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var process in processes.Skip(1)) process.Dispose();
        if (processes.FirstOrDefault() is not { } target) return;

        try
        {
            target.EnableRaisingEvents = true;
            target.Exited += (_, _) => Refresh();
            _watchedProcess = target;
        }
        catch (Exception ex)
        {
            // Sem permissao para acompanhar (nao deveria: mesma conta). A moldura ainda some
            // quando o Stream Live encerra normalmente.
            FileLog.Write($"Stream Live: sem acompanhar o processo: {ex.Message}");
            target.Dispose();
        }
    }

    private void StopWatchingProcess()
    {
        _watchedProcess?.Dispose();
        _watchedProcess = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopWatchingProcess();
        }

        _stateWait.Unregister(null);
        _stateChanged.Dispose();
    }
}
