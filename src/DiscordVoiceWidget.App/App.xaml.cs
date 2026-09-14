using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DiscordVoiceWidget.Rpc;

namespace DiscordVoiceWidget.App;

public partial class App : Application
{
    private const string InstanceMutexName = "DiscordVoiceWidget.SingleInstance";

    /// <summary>Sinal para a instancia em execucao fechar sozinha (usado pelo instalador).</summary>
    private const string ExitSignalName = "DiscordVoiceWidget.Exit";

    /// <summary>Sinal para a instancia em execucao abrir as configuracoes (segundo clique no atalho).</summary>
    private const string ShowSettingsSignalName = "DiscordVoiceWidget.ShowSettings";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _exitSignal;
    private EventWaitHandle? _settingsSignal;
    private RegisteredWaitHandle? _exitWait;
    private RegisteredWaitHandle? _settingsWait;

    private OverlayWindow? _overlay;
    private GlobalHotkeys? _hotkeys;
    private string? _registeredHotkeys;
    private bool _hotkeysSuspended;
    private DiscordCredentials? _credentials;
    private bool _demoMode;
    private DemoCall? _demo;
    private WidgetSettings _settings = new();
    private VoiceSessionService? _session;
    private VoiceWidgetViewModel? _viewModel;

    /// <summary>
    /// Uma janela por barra em uso. Todas compartilham o mesmo ViewModel, entao ha uma
    /// unica conexao com o Discord e um unico estado, desenhado em cada tela.
    /// </summary>
    private readonly List<TaskbarWidgetWindow> _windows = [];
    private ShellEventsWindow? _shellEvents;
    private SettingsWindow? _settingsWindow;
    private VoiceConnectionState _state = VoiceConnectionState.Connecting;
    private DispatcherTimer? _trimTimer;
    private DispatcherTimer? _resyncTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        ConfigureRendering();

        base.OnStartup(e);

        FileLog.PruneOldFiles();
        HookUnhandledExceptions();

        // "DiscordVoiceWidget.exe --exit": pede para a instancia aberta fechar e sai.
        // O instalador usa isto antes de substituir ou remover os arquivos.
        if (e.Args.Any(arg => arg.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
        {
            SignalRunningInstance(ExitSignalName);
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Abrir o app de novo pelo Menu Iniciar leva as configuracoes da instancia
            // que ja esta rodando, em vez de um aviso de erro.
            SignalRunningInstance(ShowSettingsSignalName);
            Shutdown();
            return;
        }

        // "--demo": call ficticia, sem Discord e sem gravar configuracoes. Usado para os
        // prints do README; "--demo-muted" mostra o proprio microfone mutado.
        var demoMuted = e.Args.Any(arg => arg.Equals("--demo-muted", StringComparison.OrdinalIgnoreCase));
        _demoMode = demoMuted || e.Args.Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase));

        FileLog.Write(_demoMode ? "iniciando (modo demonstracao)" : "iniciando");
        ListenForSignals();

        _credentials = _demoMode ? null : LoadCredentials();
        if (!_demoMode && _credentials is not { IsFilled: true })
        {
            _credentials = AskCredentials(_credentials, owner: null);
            if (_credentials is null)
            {
                Shutdown();
                return;
            }
        }

        _settings = WidgetSettings.Load();

        _viewModel = new VoiceWidgetViewModel();

        // Os widgets vivem dentro da barra (janelas filhas) e nao recebem avisos do
        // sistema; uma janela oculta de nivel superior recebe e repassa.
        _shellEvents = new ShellEventsWindow();
        _shellEvents.DisplayChanged += () =>
        {
            foreach (var window in _windows) window.OnDisplayChanged();
        };
        _shellEvents.TaskbarRecreated += () =>
        {
            foreach (var window in _windows) window.OnTaskbarRecreated();
            ScheduleWindowResync();
        };

        // Cria e mostra as janelas conforme a configuracao de monitores e overlay.
        ApplySettings(_settings, persist: false);

        // Limpeza de avatares antigos fora da thread de UI e sem pressa.
        _ = Task.Run(AvatarCache.PruneDisk);

        // Inicializacao do WPF deixa bastante memoria residente que nao volta a ser
        // usada; devolve depois que conexao, avatares e primeiro desenho assentaram.
        ScheduleMemoryTrim(TimeSpan.FromSeconds(20));

        if (_demoMode)
        {
            _demo = new DemoCall(_viewModel, demoMuted);
            OnStateChanged(VoiceConnectionState.InCall, null);
            _demo.Start();
            return;
        }

        await StartSessionAsync();
    }

    /// <summary>
    /// Ajustes globais de renderizacao, antes de qualquer janela existir.
    /// </summary>
    private static void ConfigureRendering()
    {
        // Renderizacao por software. Por padrao o WPF abre um dispositivo Direct3D, o
        // que carrega o driver da placa de video (no AMD medido: ~39 MB so do driver)
        // dentro do processo e disputa a GPU com o jogo. Uma pilula de 200x40 px
        // desenhada so quando muda nao tem nada a ganhar com GPU - e com
        // AllowsTransparency o quadro ainda teria de voltar da GPU para a CPU a cada
        // desenho.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Animacoes a 30 fps em vez de 60. Os fades duram 80-200 ms: a diferenca e
        // imperceptivel e cada quadro a menos e um redesenho a menos.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(Timeline),
            new FrameworkPropertyMetadata { DefaultValue = 30 });
    }

    private void ScheduleMemoryTrim(TimeSpan delay)
    {
        // Um unico agendamento pendente: pedidos em sequencia viram um so.
        _trimTimer ??= CreateTrimTimer();
        _trimTimer.Stop();
        _trimTimer.Interval = delay;
        _trimTimer.Start();
    }

    private DispatcherTimer CreateTrimTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            MemoryTrimmer.Trim();
        };
        return timer;
    }

    // -----------------------------------------------------------------------
    // Sessao de voz
    // -----------------------------------------------------------------------

    private async Task StartSessionAsync()
    {
        var session = new VoiceSessionService(_credentials!)
        {
            SpeakingReleaseDelay = TimeSpan.FromMilliseconds(_settings.SpeakingReleaseMs),
        };
        _session = session;

        session.Log += FileLog.Write;

        // Todos os eventos chegam na thread do pipe: marshalar antes de tocar na UI.
        session.ParticipantsChanged += participants =>
            Post(() => _viewModel!.Sync(participants, session.SelfUserId));

        session.SpeakingChanged += (userId, speaking) =>
            Post(() => _viewModel!.SetSpeaking(userId, speaking));

        session.SelfVoiceSettingsChanged += (muted, deafened) => Post(() =>
        {
            _viewModel!.SelfMuted = muted;
            _viewModel.SelfDeafened = deafened;
        });

        session.StateChanged += (state, detail) => Post(() => OnStateChanged(state, detail));

        session.CredentialsRejected += () => Post(async () =>
        {
            // Evento atrasado de uma sessao que ja foi trocada: nada a corrigir.
            if (_session != session) return;
            await OnCredentialsRejectedAsync(session);
        });

        await session.StartAsync();
    }

    /// <summary>
    /// Secret ou redirect errado. O supervisor tentaria de novo sem fim, e cada rodada
    /// abriria outro pedido de autorizacao no Discord; entao a sessao para e a janela de
    /// credenciais abre ja explicando o problema.
    /// </summary>
    private async Task OnCredentialsRejectedAsync(VoiceSessionService session)
    {
        _session = null;
        await session.DisposeAsync();
        OnStateChanged(VoiceConnectionState.Disconnected, null);

        var updated = AskCredentials(
            _credentials,
            owner: _settingsWindow,
            error: "O Discord recusou o Client Secret. Gere um novo em OAuth2 → Reset Secret e confira se o redirect https://localhost está salvo no portal.");

        if (updated is null)
        {
            Notify("Widget pausado", "O Discord recusou as credenciais. Corrija em Configurações → Trocar app do Discord.");
            return;
        }

        await ApplyCredentialsAsync(updated);
    }

    private void OnStateChanged(VoiceConnectionState state, string? detail)
    {
        var previous = _state;
        _state = state;

        FileLog.Write($"estado: {state}{(detail is null ? "" : $" ({detail})")}");

        _viewModel!.IsInCall = state == VoiceConnectionState.InCall;
        _viewModel.StatusText = state switch
        {
            VoiceConnectionState.Connecting => "Conectando...",
            VoiceConnectionState.NeedsAuthorization => "Autorize no Discord",
            VoiceConnectionState.Connected => "Fora de call",
            VoiceConnectionState.Disconnected => detail is null ? "Discord fechado" : "Reconectando...",
            _ => string.Empty,
        };

        // Fora de call o widget costuma estar escondido; sem este aviso o modal de
        // autorizacao abriria no Discord sem nenhuma pista do porque.
        if (state == VoiceConnectionState.NeedsAuthorization && previous != state)
        {
            Notify("Autorização necessária", "Um pedido de autorização abriu no Discord. Clique em Autorizar.");
        }

        // O tamanho muda junto com o conteudo; reancorar.
        foreach (var window in _windows) window.Reposition();
    }

    /// <summary>
    /// Aviso que precisa ser visto mesmo com o widget escondido (fora de call nada
    /// dele aparece na tela). Enfileirado no Dispatcher: a caixa roda um laco de
    /// mensagens proprio e nao deve abrir no meio de uma mudanca de estado.
    /// </summary>
    private void Notify(string title, string text)
    {
        FileLog.Write($"aviso: {title} - {text}");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            MessageBox.Show(text, $"Discord Voice Widget · {title}", MessageBoxButton.OK, MessageBoxImage.Information));
    }

    private async Task RestartSessionAsync()
    {
        if (_demoMode) return;

        FileLog.Write("reconexao pedida pelo usuario");
        if (_session is { } session)
        {
            // Nulo antes de aguardar: o DisposeAsync nao pode rodar duas vezes na mesma sessao.
            _session = null;
            await session.DisposeAsync();
        }
        await StartSessionAsync();
    }

    // -----------------------------------------------------------------------
    // Credenciais do app do Discord
    // -----------------------------------------------------------------------

    /// <summary>Um config.json editado a mao e quebrado vale como ausente: a janela pede de novo.</summary>
    private static DiscordCredentials? LoadCredentials()
    {
        try
        {
            return DiscordCredentials.LoadOrBootstrap();
        }
        catch (Exception ex)
        {
            FileLog.Write($"config.json ilegivel: {ex.Message}");
            return null;
        }
    }

    /// <summary>Mostra a janela de credenciais e grava o resultado. Nulo se cancelar ou nao gravar.</summary>
    private static DiscordCredentials? AskCredentials(DiscordCredentials? current, Window? owner, string? error = null)
    {
        var dialog = new CredentialsWindow(current, error);
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return null;

        try
        {
            result.Save();
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"falha ao salvar config.json: {ex.Message}");
            MessageBox.Show(
                $"Não foi possível salvar as credenciais em {AppPaths.Config}:\n{ex.Message}",
                "Discord Voice Widget",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return null;
        }
    }

    private async void ChangeCredentials()
    {
        if (_demoMode)
        {
            // So a janela, vazia como na primeira execucao; nada e gravado.
            new CredentialsWindow(null) { Owner = _settingsWindow }.ShowDialog();
            return;
        }

        if (AskCredentials(_credentials, owner: _settingsWindow) is { } updated)
        {
            await ApplyCredentialsAsync(updated);
        }
    }

    private async Task ApplyCredentialsAsync(DiscordCredentials updated)
    {
        // O token pertence ao app que o emitiu: com outro Application ID ele so seria recusado.
        if (_credentials?.ClientId != updated.ClientId) OAuthTokenStore.Clear();

        _credentials = updated;
        FileLog.Write("credenciais do Discord atualizadas");
        await RestartSessionAsync();
    }

    // -----------------------------------------------------------------------
    // Configuracoes
    // -----------------------------------------------------------------------

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.SettingsChanged += updated =>
        {
            // A posicao do overlay nao se edita nesta tela; a que vale e a atual, que pode
            // ter mudado por arrasto enquanto a tela estava aberta.
            updated.OverlayPlacement = _settings.Clone().OverlayPlacement;
            ApplySettings(updated, persist: true);
        };
        _settingsWindow.HotkeyRecordingChanged += recording =>
        {
            _hotkeysSuspended = recording;
            RegisterHotkeys(force: true);
        };
        _settingsWindow.MoveOverlayRequested += ToggleOverlayMove;
        _settingsWindow.CredentialsChangeRequested += ChangeCredentials;
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _hotkeysSuspended = false;
            RegisterHotkeys(force: true);

            // A tela de configuracoes carrega o tema Fluent inteiro; depois de fechada
            // esses recursos podem voltar ao sistema.
            ScheduleMemoryTrim(TimeSpan.FromSeconds(5));
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ApplySettings(WidgetSettings settings, bool persist)
    {
        _settings = settings;

        SyncWindows(settings);
        SyncOverlay(settings);
        RegisterHotkeys(force: false);
        _viewModel!.ApplyAvatarSize(settings.AvatarSize);

        if (_session is not null)
        {
            _session.SpeakingReleaseDelay = TimeSpan.FromMilliseconds(settings.SpeakingReleaseMs);
        }

        if (!persist || _demoMode) return;

        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            FileLog.Write($"falha ao salvar configuracoes: {ex.Message}");
        }
    }

    /// <summary>
    /// Deixa exatamente uma janela por barra escolhida, reaproveitando as que ja existem
    /// para a troca de opcao nao piscar o widget.
    ///
    /// No modo das duas telas a janela da secundaria usa SecondaryOnly: sem segundo
    /// monitor ela fica escondida em vez de recair na principal, onde ja ha a outra.
    /// </summary>
    private void SyncWindows(WidgetSettings settings)
    {
        TaskbarTarget[] targets = settings.Monitor switch
        {
            TaskbarMonitorPreference.Both => [TaskbarTarget.SecondaryOnly, TaskbarTarget.Primary],
            TaskbarMonitorPreference.Primary => [TaskbarTarget.Primary],
            _ => [TaskbarTarget.Secondary],
        };

        while (_windows.Count > targets.Length)
        {
            var extra = _windows[^1];
            _windows.RemoveAt(_windows.Count - 1);
            extra.Close();
        }

        for (var i = 0; i < targets.Length; i++)
        {
            var isNew = i >= _windows.Count;
            var window = isNew ? CreateWidgetWindow() : _windows[i];

            window.Target = targets[i];
            window.HideWhenNotInCall = settings.HideWhenNotInCall;

            if (!isNew) continue;

            _windows.Add(window);
            window.Show();
        }
    }

    /// <summary>
    /// Uma janela da barra fechou sem o app pedir: o explorer reiniciou, a barra foi
    /// destruida e levou a janela filha junto. Recria depois que a barra nova existir.
    /// </summary>
    private void OnWidgetWindowClosed(TaskbarWidgetWindow window)
    {
        // Fechada de proposito por SyncWindows (ja saiu da lista).
        if (!_windows.Contains(window)) return;

        // Nunca foi embutida, ou a barra ainda existe: e o encerramento normal do app.
        if (window.CurrentTaskbar is not { } taskbar || NativeMethods.IsWindow(taskbar.Handle)) return;

        _windows.Remove(window);
        FileLog.Write("barra de tarefas destruida (explorer reiniciou); recriando o widget");
        ScheduleWindowResync();
    }

    private void ScheduleWindowResync()
    {
        // Um instante para o explorer terminar de montar as barras novas; se ainda nao
        // estiverem prontas, a janela recriada tenta de novo a cada 2 s.
        _resyncTimer ??= CreateOneShotTimer(TimeSpan.FromSeconds(2), () => SyncWindows(_settings));
        _resyncTimer.Stop();
        _resyncTimer.Start();
    }

    private static DispatcherTimer CreateOneShotTimer(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    // -----------------------------------------------------------------------
    // Overlay nos jogos
    // -----------------------------------------------------------------------

    /// <summary>
    /// Cria ou fecha a janela do overlay. Desligado, ele nao existe: nenhuma janela,
    /// nenhum hook, nenhum custo.
    /// </summary>
    private void SyncOverlay(WidgetSettings settings)
    {
        if (!settings.OverlayEnabled)
        {
            if (_overlay is null) return;

            _overlay.Close();
            _overlay = null;
            ScheduleMemoryTrim(TimeSpan.FromSeconds(10));
            return;
        }

        var created = _overlay is null;
        if (created)
        {
            _overlay = new OverlayWindow(_viewModel!);
            _overlay.PlacementChanged += OnOverlayPlacementChanged;
            _overlay.ActiveChanged += (_, active) =>
            {
                if (!active) ScheduleMemoryTrim(TimeSpan.FromSeconds(10));
            };
        }

        _overlay!.HideWhenNotInCall = settings.HideWhenNotInCall;
        _overlay.MoveHotkeyText = settings.MoveOverlayHotkey;
        _overlay.SetPlacement(settings.OverlayPlacement);

        if (created) _overlay.Show();
    }

    /// <summary>Liga/desliga pelo atalho ou pelo menu do widget.</summary>
    private void ToggleOverlay()
    {
        var updated = _settings.Clone();
        updated.OverlayEnabled = !updated.OverlayEnabled;

        if (!updated.OverlayEnabled && _overlay?.IsMoving == true) _overlay.EndMove();

        ApplySettings(updated, persist: true);
        _settingsWindow?.ReflectExternalChange(_settings);

        // Sem jogo aberto o overlay recem-ligado ficaria invisivel; aparecer por um
        // instante confirma que ligou e mostra onde esta.
        if (updated.OverlayEnabled) _overlay?.Peek();

        FileLog.Write($"overlay {(updated.OverlayEnabled ? "ligado" : "desligado")}");
    }

    /// <summary>Entra ou sai do modo de posicionar. Liga o overlay antes, se preciso.</summary>
    private void ToggleOverlayMove()
    {
        if (!_settings.OverlayEnabled)
        {
            var updated = _settings.Clone();
            updated.OverlayEnabled = true;
            ApplySettings(updated, persist: true);
            _settingsWindow?.ReflectExternalChange(_settings);
        }

        if (_overlay is null) return;

        if (_overlay.IsMoving) _overlay.EndMove();
        else _overlay.BeginMove();
    }

    private void OnOverlayPlacementChanged(OverlayPlacement placement)
    {
        _settings.OverlayPlacement = placement;
        _settingsWindow?.ReflectExternalChange(_settings);

        if (_demoMode) return;

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            FileLog.Write($"falha ao salvar posicao do overlay: {ex.Message}");
        }
    }

    private OverlayMenuState CurrentOverlayMenuState() => new(
        _settings.OverlayEnabled,
        _overlay?.IsMoving == true,
        _settings.ToggleOverlayHotkey,
        _settings.MoveOverlayHotkey);

    /// <summary>
    /// (Re)registra os atalhos globais. So mexe quando os atalhos mudaram, salvo
    /// <paramref name="force"/> - usado ao pausar/retomar durante a gravacao de um atalho.
    /// </summary>
    private void RegisterHotkeys(bool force)
    {
        var signature = $"{_settings.ToggleOverlayHotkey}|{_settings.MoveOverlayHotkey}|{_hotkeysSuspended}";
        if (!force && signature == _registeredHotkeys) return;
        _registeredHotkeys = signature;

        _hotkeys ??= new GlobalHotkeys();
        _hotkeys.UnregisterAll();
        if (_hotkeysSuspended) return;

        var conflicts = new List<string>(2);

        var toggle = HotkeyBinding.Parse(_settings.ToggleOverlayHotkey);
        if (!_hotkeys.Register(toggle, ToggleOverlay)) conflicts.Add(toggle.ToString());

        var move = HotkeyBinding.Parse(_settings.MoveOverlayHotkey);
        if (!_hotkeys.Register(move, ToggleOverlayMove)) conflicts.Add(move.ToString());

        if (conflicts.Count == 0 || force) return;

        // Outro programa (ou os dois atalhos iguais) ja ocupa a combinacao.
        var list = string.Join(", ", conflicts);
        FileLog.Write($"atalho indisponivel: {list}");
        Notify("Atalho indisponível", $"{list} já está em uso por outro programa. Escolha outro nas configurações.");
    }

    // -----------------------------------------------------------------------
    // Sinais entre instancias
    // -----------------------------------------------------------------------

    /// <summary>
    /// Espera pelos sinais de outras instancias sem nenhuma thread parada: o proprio
    /// Windows avisa o pool de threads quando o evento e disparado.
    /// </summary>
    private void ListenForSignals()
    {
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsSignalName);

        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitSignal,
            (_, _) => Dispatcher.BeginInvoke(() => Shutdown()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _settingsWait = ThreadPool.RegisterWaitForSingleObject(
            _settingsSignal,
            (_, _) => Dispatcher.BeginInvoke(OpenSettings),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalRunningInstance(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return;

        using (handle)
        {
            handle.Set();
        }
    }

    private TaskbarWidgetWindow CreateWidgetWindow()
    {
        var window = new TaskbarWidgetWindow(_viewModel!);
        window.ReconnectRequested += async (_, _) => await RestartSessionAsync();
        window.SettingsRequested += (_, _) => OpenSettings();
        window.OverlayToggleRequested += (_, _) => ToggleOverlay();
        window.OverlayMoveRequested += (_, _) => ToggleOverlayMove();
        window.OverlayStateProvider = CurrentOverlayMenuState;
        window.Closed += (_, _) => OnWidgetWindowClosed(window);

        // Saiu da call: a lista de participantes e os avatares ja nao estao na tela.
        // Com duas janelas o pedido chega duas vezes; o agendamento junta em um so.
        window.ActiveChanged += (_, active) =>
        {
            if (!active) ScheduleMemoryTrim(TimeSpan.FromSeconds(10));
        };

        return window;
    }

    // -----------------------------------------------------------------------
    // Infraestrutura
    // -----------------------------------------------------------------------

    private void HookUnhandledExceptions()
    {
        // Um widget de barra nao deve sumir por causa de um erro pontual de UI:
        // registra e segue. Falhas fora do Dispatcher so podem ser registradas.
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Write($"erro nao tratado na UI: {args.Exception}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            FileLog.Write($"erro fatal: {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Write($"task com erro nao observado: {args.Exception}");
            args.SetObserved();
        };
    }

    private void Post(Action action) => Dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);

    protected override void OnExit(ExitEventArgs e)
    {
        // Instancias que so repassaram um sinal (--exit, segundo clique) nao chegaram a
        // iniciar nada e nao devem aparecer no log como encerramento do widget.
        if (_exitSignal is not null) FileLog.Write("encerrando");

        _shellEvents?.Dispose();
        _hotkeys?.Dispose();
        _exitWait?.Unregister(null);
        _settingsWait?.Unregister(null);
        _exitSignal?.Dispose();
        _settingsSignal?.Dispose();

        if (_session is { } session)
        {
            // Fora do Dispatcher: as continuacoes do DisposeAsync nao podem depender
            // da thread de UI que esta sendo encerrada, senao o Wait trava.
            Task.Run(() => session.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2));
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
