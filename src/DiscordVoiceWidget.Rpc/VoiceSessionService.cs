using System.Text.Json;

namespace DiscordVoiceWidget.Rpc;

/// <summary>
/// Traduz o RPC cru do Discord em estado de voz pronto para a interface.
///
/// Cuida de: autenticacao (com refresh), reconexao com backoff, troca de canal
/// (subscribe/unsubscribe) e do debounce dos eventos de fala.
///
/// Os eventos sao disparados na thread do loop de leitura do pipe. Quem consome
/// na UI precisa marshalar para o Dispatcher.
/// </summary>
public sealed class VoiceSessionService : IAsyncDisposable
{
    /// <summary>
    /// rpc.voice.write entrou na 1.0.4, para mutar e ensurdecer pelo menu. Tokens salvos
    /// antes dela nao o tem: o AUTHENTICATE passa, mas o SET_VOICE_SETTINGS seria
    /// recusado, entao um token sem algum destes escopos pede autorizacao de novo.
    /// </summary>
    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write", "identify"];

    private static readonly string[] ChannelEvents =
    [
        "VOICE_STATE_CREATE",
        "VOICE_STATE_UPDATE",
        "VOICE_STATE_DELETE",
        "SPEAKING_START",
        "SPEAKING_STOP",
    ];

    private readonly DiscordCredentials _credentials;
    private readonly object _gate = new();
    private readonly Dictionary<string, VoiceParticipant> _participants = [];
    private readonly Dictionary<string, SpeakerRelease> _releases = [];
    private readonly HashSet<string> _speaking = [];

    /// <summary>
    /// Uma troca de canal por vez. Ser movido de call gera varios eventos quase juntos
    /// (VOICE_STATE_DELETE do canal antigo, VOICE_CHANNEL_SELECT, as vezes um null no
    /// meio); trocas concorrentes se atropelavam no unsubscribe/subscribe e o widget
    /// podia terminar preso no canal errado ou fora de call.
    /// </summary>
    private readonly SemaphoreSlim _channelGate = new(1, 1);

    private DiscordIpcClient? _ipc;
    private CancellationTokenSource? _lifetime;
    private Task? _supervisor;
    private string? _channelId;

    public VoiceSessionService(DiscordCredentials credentials) => _credentials = credentials;

    /// <summary>
    /// Quanto segurar o anel aceso depois de um SPEAKING_STOP.
    ///
    /// O Discord emite SPEAKING_START/STOP em rajadas durante a fala normal. Medido
    /// na validacao: pausas de 150-400 ms no meio de uma unica frase. Sem segurar a
    /// queda, o anel pisca de forma erratica em vez de acompanhar a voz; segurando
    /// demais, ele fica aceso depois que a pessoa parou. 350 ms cobre as pausas
    /// observadas sem atraso perceptivel.
    /// </summary>
    public TimeSpan SpeakingReleaseDelay { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Seu proprio user id, conhecido a partir do evento READY.</summary>
    public string? SelfUserId { get; private set; }

    public VoiceConnectionState State { get; private set; } = VoiceConnectionState.Disconnected;

    public event Action<IReadOnlyList<VoiceParticipant>>? ParticipantsChanged;
    public event Action<string, bool>? SpeakingChanged;
    public event Action<bool, bool>? SelfVoiceSettingsChanged;
    public event Action<VoiceConnectionState, string?>? StateChanged;
    public event Action<string>? Log;

    /// <summary>O Discord recusou client secret ou redirect ao trocar o code pelo token.</summary>
    public event Action? CredentialsRejected;

    public IReadOnlyList<VoiceParticipant> Participants
    {
        get { lock (_gate) return [.. _participants.Values]; }
    }

    /// <summary>Sobe o supervisor em segundo plano. Retorna assim que ele inicia.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _supervisor = Task.Run(() => SuperviseAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Supervisao e reconexao
    // -----------------------------------------------------------------------

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(VoiceConnectionState.Disconnected, ex.Message);
                Log?.Invoke($"sessao caiu: {ex.Message}");
                if (ex is DiscordCredentialsRejectedException) CredentialsRejected?.Invoke();
            }

            ResetVoiceState();

            if (ct.IsCancellationRequested) break;

            Log?.Invoke($"reconectando em {backoff.TotalSeconds:0}s");
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }

            backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
        }
    }

    /// <summary>Uma sessao completa. So retorna quando a conexao cai.</summary>
    private async Task RunSessionAsync(CancellationToken ct)
    {
        SetState(VoiceConnectionState.Connecting);

        await using var ipc = new DiscordIpcClient();
        _ipc = ipc;

        // Inscricoes nao sobrevivem a um pipe novo: sem zerar, a reconexao achava que ja
        // acompanhava o canal e nunca se inscrevia de novo.
        _channelId = null;

        var died = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        ipc.Faulted += ex => died.TrySetResult(ex);
        ipc.Log += m => Log?.Invoke(m);
        ipc.EventReceived += OnEventReceived;

        var ready = await ipc.ConnectAsync(_credentials.ClientId, ct);
        SelfUserId = ready.TryGetProperty("user", out var u) && u.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;

        await AuthenticateAsync(ipc, ct);
        SetState(VoiceConnectionState.Connected);

        await ipc.SubscribeAsync("VOICE_CHANNEL_SELECT", ct: ct);
        await ipc.SubscribeAsync("VOICE_SETTINGS_UPDATE", ct: ct);

        // Estado inicial: talvez ja estejamos em uma call.
        var settings = await ipc.CommandAsync("GET_VOICE_SETTINGS", ct: ct);
        RaiseSelfSettings(settings);

        await SyncChannelAsync(ct);

        // Fica vivo ate o pipe morrer ou o app encerrar.
        var faulted = await died.Task.WaitAsync(ct);
        throw faulted;
    }

    private async Task AuthenticateAsync(DiscordIpcClient ipc, CancellationToken ct)
    {
        var stored = OAuthTokenStore.Load();

        if (stored is not null && stored.NeedsRefresh && stored.RefreshToken is not null)
        {
            try
            {
                stored = await DiscordOAuth.RefreshAsync(_credentials, stored.RefreshToken, ct);
                OAuthTokenStore.Save(stored);
                Log?.Invoke("token renovado");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"refresh falhou ({ex.Message}) - autorizando de novo");
                stored = null;
            }
        }

        if (stored is not null)
        {
            JsonElement? auth = null;
            try
            {
                auth = await ipc.CommandAsync("AUTHENTICATE", AccessTokenArgs(stored.AccessToken), ct: ct);
            }
            catch (DiscordRpcException ex)
            {
                Log?.Invoke($"token recusado ({ex.Message}) - autorizando de novo");
                OAuthTokenStore.Clear();
            }

            if (auth is { } granted)
            {
                if (HasAllScopes(granted)) return;

                // Esta conexao ja esta autenticada e o Discord recusa um AUTHORIZE nela
                // ("4002: Already authenticated"). Sem o token, a proxima sessao autoriza
                // num pipe novo.
                OAuthTokenStore.Clear();
                throw new DiscordRpcException("token sem todos os escopos - autorizando de novo");
            }
        }

        SetState(VoiceConnectionState.NeedsAuthorization);

        // Nao envie redirect_uri aqui: o fluxo RPC recusa o campo e usa sozinho o
        // primeiro redirect cadastrado no portal. Ele so entra na troca do code.
        var authorize = await ipc.CommandAsync(
            "AUTHORIZE",
            w =>
            {
                w.WriteString("client_id", _credentials.ClientId);
                w.WriteStartArray("scopes");
                foreach (var scope in Scopes) w.WriteStringValue(scope);
                w.WriteEndArray();
            },
            timeout: TimeSpan.FromMinutes(3),
            ct: ct);

        var code = authorize.GetProperty("code").GetString()
                   ?? throw new DiscordRpcException("AUTHORIZE nao devolveu um code.");

        var token = await DiscordOAuth.ExchangeCodeAsync(_credentials, code, ct);
        OAuthTokenStore.Save(token);

        await ipc.CommandAsync("AUTHENTICATE", AccessTokenArgs(token.AccessToken), ct: ct);
    }

    /// <summary>Resposta do AUTHENTICATE sem a lista de escopos vale como completa.</summary>
    private static bool HasAllScopes(JsonElement auth)
    {
        if (!auth.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array) return true;

        var granted = scopes.EnumerateArray().Select(s => s.GetString()).ToHashSet();
        return Scopes.All(granted.Contains);
    }

    private static Action<Utf8JsonWriter> AccessTokenArgs(string accessToken)
        => w => w.WriteString("access_token", accessToken);

    private static Action<Utf8JsonWriter> ChannelArgs(string channelId)
        => w => w.WriteString("channel_id", channelId);

    // -----------------------------------------------------------------------
    // Meu microfone e fone
    // -----------------------------------------------------------------------

    /// <summary>
    /// Muta/desmuta ou ensurdece/desensurdece a propria conta. O estado novo volta pelo
    /// VOICE_SETTINGS_UPDATE, como quando a mudanca e feita no proprio Discord.
    /// </summary>
    public async Task SetSelfVoiceAsync(bool? mute = null, bool? deaf = null, CancellationToken ct = default)
    {
        if (_ipc is not { } ipc || State is VoiceConnectionState.Disconnected or VoiceConnectionState.Connecting)
        {
            throw new InvalidOperationException("Sem conexao com o Discord.");
        }

        await ipc.CommandAsync(
            "SET_VOICE_SETTINGS",
            w =>
            {
                if (mute is { } m) w.WriteBoolean("mute", m);
                if (deaf is { } d) w.WriteBoolean("deaf", d);
            },
            ct: ct);
    }

    // -----------------------------------------------------------------------
    // Canal de voz
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pergunta ao Discord em que canal estamos e se alinha a ele.
    ///
    /// O id que vem no evento nao e usado: com eventos enfileirados ele pode ja estar
    /// velho (movido duas vezes seguidas, ou o null intermediario de uma troca). O que
    /// vale e a resposta do GET_SELECTED_VOICE_CHANNEL no momento em que a troca roda.
    /// </summary>
    private async Task SyncChannelAsync(CancellationToken ct)
    {
        await _channelGate.WaitAsync(ct);
        try
        {
            if (_ipc is not { } ipc) return;

            var channel = await ipc.CommandAsync("GET_SELECTED_VOICE_CHANNEL", ct: ct);
            var currentId = channel.ValueKind == JsonValueKind.Object
                            && channel.TryGetProperty("id", out var cid)
                            && cid.ValueKind == JsonValueKind.String
                ? cid.GetString()
                : null;

            await BindChannelAsync(ipc, currentId, ct);
        }
        finally
        {
            _channelGate.Release();
        }
    }

    /// <summary>Dispara uma sincronizacao a partir do loop de leitura, sem bloquea-lo.</summary>
    private void RequestChannelSync()
    {
        var ct = _lifetime?.Token ?? default;
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncChannelAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // Sem isto a falha sumia numa task nao observada e o widget ficava no
                // estado da troca pela metade.
                Log?.Invoke($"troca de canal falhou: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private async Task BindChannelAsync(DiscordIpcClient ipc, string? channelId, CancellationToken ct)
    {
        if (_channelId == channelId) return;

        if (_channelId is not null)
        {
            // Sem o unsubscribe, eventos de um canal ja abandonado continuam chegando.
            foreach (var evt in ChannelEvents)
            {
                try { await ipc.UnsubscribeAsync(evt, ChannelArgs(_channelId), ct); }
                catch (Exception ex) { Log?.Invoke($"unsubscribe {evt}: {ex.Message}"); }
            }
        }

        _channelId = channelId;
        ResetVoiceState();

        if (channelId is null)
        {
            SetState(VoiceConnectionState.Connected);
            ParticipantsChanged?.Invoke([]);
            return;
        }

        try
        {
            foreach (var evt in ChannelEvents)
            {
                await ipc.SubscribeAsync(evt, ChannelArgs(channelId), ct);
            }
        }
        catch
        {
            // Inscricao pela metade: esquecer o canal para a proxima sincronizacao refazer tudo.
            _channelId = null;
            SetState(VoiceConnectionState.Connected);
            ParticipantsChanged?.Invoke([]);
            throw;
        }

        var channel = await ipc.CommandAsync("GET_SELECTED_VOICE_CHANNEL", ct: ct);
        if (channel.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
        {
            lock (_gate)
            {
                foreach (var state in states.EnumerateArray())
                {
                    if (VoiceParticipant.From(state) is { } p) _participants[p.UserId] = p;
                }
            }
        }

        var name = channel.TryGetProperty("name", out var n) ? n.GetString() : channelId;
        Log?.Invoke($"entrou em \"{name}\" ({channelId})");

        SetState(VoiceConnectionState.InCall);
        ParticipantsChanged?.Invoke(Participants);
    }

    private void OnEventReceived(string evt, JsonElement data)
    {
        switch (evt)
        {
            case "SPEAKING_START":
                if (UserIdOf(data) is { } starting) OnSpeaking(starting, true);
                return;

            case "SPEAKING_STOP":
                if (UserIdOf(data) is { } stopping) OnSpeaking(stopping, false);
                return;

            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
            {
                if (VoiceParticipant.From(data) is not { } p) return;
                lock (_gate) _participants[p.UserId] = p;
                ParticipantsChanged?.Invoke(Participants);
                return;
            }

            case "VOICE_STATE_DELETE":
            {
                if (VoiceParticipant.From(data) is not { } p) return;

                // Eu sai do canal que o widget acompanha. Normalmente vem junto um
                // VOICE_CHANNEL_SELECT, mas ao ser movido por outra pessoa ele pode
                // atrasar ou nao vir; conferir com o Discord cobre os dois casos.
                if (p.UserId == SelfUserId) RequestChannelSync();

                lock (_gate)
                {
                    _participants.Remove(p.UserId);
                    _speaking.Remove(p.UserId);
                    if (_releases.Remove(p.UserId, out var release)) release.Timer.Dispose();
                }
                ParticipantsChanged?.Invoke(Participants);
                return;
            }

            case "VOICE_CHANNEL_SELECT":
                RequestChannelSync();
                return;

            case "VOICE_SETTINGS_UPDATE":
                RaiseSelfSettings(data);
                return;
        }
    }

    private void RaiseSelfSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object) return;
        var muted = settings.TryGetProperty("mute", out var m) && m.ValueKind == JsonValueKind.True;
        var deafened = settings.TryGetProperty("deaf", out var d) && d.ValueKind == JsonValueKind.True;
        SelfVoiceSettingsChanged?.Invoke(muted, deafened);
    }

    // -----------------------------------------------------------------------
    // Debounce da fala
    // -----------------------------------------------------------------------

    /// <summary>
    /// Queda pendente do anel de uma pessoa. Um timer por pessoa, criado uma vez e
    /// reprogramado a cada SPEAKING_STOP: numa call ativa chegam varios eventos por
    /// segundo, e criar CancellationTokenSource + Task.Delay a cada um gerava lixo
    /// continuo para o GC.
    /// </summary>
    private sealed class SpeakerRelease(Timer timer)
    {
        public Timer Timer { get; } = timer;

        /// <summary>Instante (Environment.TickCount64) em que o anel apaga; 0 = nada pendente.</summary>
        public long DeadlineMs { get; set; }
    }

    private void OnSpeaking(string userId, bool speaking)
    {
        bool changed;
        lock (_gate)
        {
            if (!speaking)
            {
                // Nao apaga na hora: agenda a queda e ve se a pessoa volta a falar antes.
                if (!_speaking.Contains(userId)) return;

                if (!_releases.TryGetValue(userId, out var release))
                {
                    release = new SpeakerRelease(new Timer(OnReleaseDue, userId, Timeout.Infinite, Timeout.Infinite));
                    _releases[userId] = release;
                }

                var delay = SpeakingReleaseDelay;
                release.DeadlineMs = Environment.TickCount64 + (long)delay.TotalMilliseconds;
                release.Timer.Change(delay, Timeout.InfiniteTimeSpan);
                return;
            }

            // Voltou a falar: zerar o prazo invalida um disparo que ja esteja a caminho.
            if (_releases.TryGetValue(userId, out var pending)) pending.DeadlineMs = 0;
            changed = _speaking.Add(userId);
        }

        if (changed) SpeakingChanged?.Invoke(userId, true);
    }

    private void OnReleaseDue(object? state)
    {
        var userId = (string)state!;
        bool changed;

        lock (_gate)
        {
            if (!_releases.TryGetValue(userId, out var release) || release.DeadlineMs == 0) return;

            // O timer pode disparar alguns ms antes do TickCount64 alcancar o prazo
            // (resolucoes diferentes). Ignorar esse disparo deixaria o anel aceso para
            // sempre, entao ele e reagendado pelo que falta.
            var remaining = release.DeadlineMs - Environment.TickCount64;
            if (remaining > 0)
            {
                release.Timer.Change(remaining, Timeout.Infinite);
                return;
            }

            release.DeadlineMs = 0;
            changed = _speaking.Remove(userId);
        }

        if (changed) SpeakingChanged?.Invoke(userId, false);
    }

    private void ResetVoiceState()
    {
        lock (_gate)
        {
            foreach (var release in _releases.Values) release.Timer.Dispose();
            _releases.Clear();
            _participants.Clear();
            _speaking.Clear();
        }
    }

    private static string? UserIdOf(JsonElement data)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("user_id", out var u)
            ? u.GetString()
            : null;

    private void SetState(VoiceConnectionState state, string? detail = null)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null) await _lifetime.CancelAsync();

        if (_supervisor is not null)
        {
            try { await _supervisor; } catch { /* encerramento */ }
        }

        ResetVoiceState();
        _lifetime?.Dispose();
    }
}
