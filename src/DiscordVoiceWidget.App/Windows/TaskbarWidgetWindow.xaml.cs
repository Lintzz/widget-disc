using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Janela do widget na barra de tarefas, embutida DENTRO da barra.
///
/// A janela vira filha da taskbar (WS_CHILD + SetParent), a mesma tecnica do
/// FluentFlyout. A primeira versao flutuava por cima da barra como janela topmost, e
/// isso tinha dois defeitos que so a embutida resolve:
///
/// - Piscada ao clicar na barra: a taskbar tambem e topmost e, ao ser ativada, sobe
///   para o topo e cobre o widget ate ele se reafirmar. Filha da barra, a janela e
///   desenhada sempre por cima do conteudo da propria barra.
/// - Jogo em tela cheia: a barra fica atras do jogo e leva o widget junto, sem
///   precisar detectar tela cheia.
///
/// Custos da tecnica, aceitos conscientemente:
/// - Janela filha de outro processo compartilha a fila de entrada com a thread da barra.
///   A thread de UI deste app e leve (so desenha quando algo muda), entao nao atrasa o
///   explorer.
/// - Se o explorer reiniciar, a barra e destruida e leva a janela junto. O App recria
///   as janelas quando isso acontece (ver App.OnWidgetWindowClosed).
/// - Janelas filhas nao recebem avisos do sistema (resolucao, reinicio do explorer):
///   eles chegam pela janela oculta do icone da bandeja e o App repassa.
///
/// Agendamento: fora de call nada roda. Em call, a unica sondagem e a largura de outros
/// widgets na barra (2 s) e o relogio da barra secundaria, na virada do minuto.
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    /// <summary>Reancoragem enquanto visivel: outros widgets mudam de largura sem avisar.</summary>
    private static readonly TimeSpan LayoutInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Consulta do relogio logo depois da virada do minuto, quando o texto da hora
    /// pode ter mudado de largura.
    /// </summary>
    private static readonly TimeSpan ClockCheckOffset = TimeSpan.FromMilliseconds(700);

    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _layoutTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly VoiceWidgetViewModel _viewModel;
    private readonly TaskbarClockLocator _clockLocator = new();

    private HwndSource? _source;

    private (int X, int Y)? _lastPosition;
    private bool _initialized;
    private bool _active;
    private TaskbarTarget _target = TaskbarTarget.Secondary;
    private bool _hideWhenNotInCall = true;
    private bool _shown = true;

    public TaskbarWidgetWindow(VoiceWidgetViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        _layoutTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LayoutInterval };
        _layoutTimer.Tick += OnLayoutTick;

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
        _clockTimer.Tick += OnClockTick;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        Closed += OnClosed;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Resposta da consulta ao relogio chega em outra thread.
        _clockLocator.BoundsChanged += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, Reposition);
    }

    public event EventHandler? ReconnectRequested;
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Mudou entre "trabalhando" (em call, ou configurado para ficar sempre visivel) e
    /// "parado". O app usa a transicao para devolver memoria ao sistema.
    /// </summary>
    public event EventHandler<bool>? ActiveChanged;

    /// <summary>
    /// Barra em que esta janela fica. Com um widget em cada tela, o app cria duas
    /// janelas: uma <see cref="TaskbarTarget.SecondaryOnly"/> e uma
    /// <see cref="TaskbarTarget.Primary"/>, sobre o mesmo ViewModel.
    /// </summary>
    internal TaskbarTarget Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            ForceReposition(refreshClock: true);
        }
    }

    public bool HideWhenNotInCall
    {
        get => _hideWhenNotInCall;
        set
        {
            _hideWhenNotInCall = value;
            UpdateActivity();
        }
    }

    /// <summary>Barra usada na ultima reancoragem.</summary>
    internal TaskbarInfo? CurrentTaskbar { get; private set; }

    private IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    // -----------------------------------------------------------------------
    // Ciclo de vida
    // -----------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Precisa do handle: por isso aqui e nao no construtor.
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        WindowHelper.SetNoActivate(this);

        _initialized = true;
        UpdateActivity();
    }

    /// <summary>
    /// Liga ou desliga todo o trabalho de fundo conforme o widget precise existir na
    /// tela. E o ponto que garante custo zero fora de call.
    /// </summary>
    private void UpdateActivity()
    {
        if (!_initialized) return;

        var active = !HideWhenNotInCall || _viewModel.IsInCall;
        if (active != _active)
        {
            _active = active;

            if (active)
            {
                ForceReposition(refreshClock: true);
                _layoutTimer.Start();
                ScheduleClockCheck();
            }
            else
            {
                _layoutTimer.Stop();
                _clockTimer.Stop();
            }

            ActiveChanged?.Invoke(this, active);
        }

        UpdateVisibility();
    }

    // -----------------------------------------------------------------------
    // Avisos do sistema (repassados pelo App, ja que janela filha nao os recebe)
    // -----------------------------------------------------------------------

    /// <summary>Resolucao, escala ou configuracao da barra mudou.</summary>
    public void OnDisplayChanged()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ForceReposition(refreshClock: true));

    /// <summary>Explorer reiniciou: handles e elementos de automacao antigos morreram.</summary>
    public void OnTaskbarRecreated()
    {
        _clockLocator.Reset();
        OnDisplayChanged();
    }

    // -----------------------------------------------------------------------
    // Gatilhos
    // -----------------------------------------------------------------------

    private void OnLayoutTick(object? sender, EventArgs e)
    {
        // Sem barra alvo: tenta de novo. Ao conectar um monitor, o explorer cria a barra
        // secundaria um instante depois do aviso de mudanca de tela.
        if (_shown || CurrentTaskbar is null) Reposition();
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (_active && CurrentTaskbar is { IsPrimary: false } taskbar) _clockLocator.Refresh(taskbar.Handle);
        ScheduleClockCheck();
    }

    private void ScheduleClockCheck()
    {
        var now = DateTime.Now;
        var nextMinute = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(1);

        _clockTimer.Stop();
        _clockTimer.Interval = nextMinute - now + ClockCheckOffset;
        _clockTimer.Start();
    }

    // -----------------------------------------------------------------------
    // Posicao e visibilidade
    // -----------------------------------------------------------------------

    /// <summary>
    /// Embute a janela na barra certa e a posiciona ao lado do relogio (barra
    /// secundaria) ou da bandeja (barra principal), desviando de outros widgets.
    /// Nao faz nada enquanto o widget esta parado.
    /// </summary>
    public void Reposition()
    {
        if (!_active) return;

        var taskbar = TaskbarPositionHelper.GetTaskbar(Target, _clockLocator);
        var availabilityChanged = (taskbar is null) != (CurrentTaskbar is null);
        CurrentTaskbar = taskbar;

        // A barra alvo sumiu (monitor desconectado) ou apareceu: esconde ou mostra.
        if (availabilityChanged) UpdateVisibility();
        if (taskbar is null) return;

        EnsureEmbedded(taskbar);

        if (TaskbarPositionHelper.GetPhysicalSize(this) is not { } size) return;

        var blockers = TaskbarPositionHelper.GetDockedWidgets(Handle, taskbar);
        var position = TaskbarPositionHelper.ComputePosition(taskbar, size.Width, size.Height, blockers);

        // Mover a janela gera mensagens e redesenho: so quando mudou de fato.
        if (_lastPosition == position) return;
        _lastPosition = position;

        // Janela filha: coordenadas relativas a area cliente da barra, nao a tela.
        var client = new POINT { X = position.X, Y = position.Y };
        ScreenToClient(taskbar.Handle, ref client);

        // HWND_TOP entre as filhas da barra: acima do conteudo XAML da propria taskbar.
        SetWindowPos(Handle, HWND_TOP, client.X, client.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Torna a janela filha da barra alvo. Roda na primeira posicao, quando a barra
    /// alvo muda (troca de monitor) e depois de o explorer recriar a barra.
    /// </summary>
    private void EnsureEmbedded(TaskbarInfo taskbar)
    {
        if (Handle == IntPtr.Zero || GetParent(Handle) == taskbar.Handle) return;

        // Sem trocar POPUP por CHILD, o SetParent deixa uma janela de nivel superior
        // "presa" a outra, com ordem e recorte imprevisiveis.
        var style = GetWindowLong(Handle, GWL_STYLE);
        SetWindowLong(Handle, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
        SetParent(Handle, taskbar.Handle);

        _lastPosition = null;
    }

    private void ForceReposition(bool refreshClock)
    {
        if (!_active) return;

        _lastPosition = null;
        Reposition();

        if (refreshClock && CurrentTaskbar is { IsPrimary: false } taskbar) _clockLocator.Refresh(taskbar.Handle);
    }

    /// <summary>
    /// Mostra ou esconde com um fade.
    ///
    /// Esconder e zerar a opacidade, nao chamar Hide(): numa janela com
    /// AllowsTransparency, pixel totalmente transparente ja deixa o clique passar para a
    /// barra por baixo.
    /// </summary>
    private void UpdateVisibility()
    {
        var shouldShow = _active && CurrentTaskbar is not null;

        if (shouldShow == _shown) return;
        _shown = shouldShow;

        if (shouldShow) Reposition();

        View.IsHitTestVisible = shouldShow;
        View.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(shouldShow ? 1 : 0, FadeDuration) { EasingFunction = new QuadraticEase() });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VoiceWidgetViewModel.IsInCall)) UpdateActivity();
    }

    // -----------------------------------------------------------------------
    // Menu
    // -----------------------------------------------------------------------

    private void OnSettingsClick(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnReconnectClick(object sender, RoutedEventArgs e)
        => ReconnectRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitClick(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();

    private void OnClosed(object? sender, EventArgs e)
    {
        _layoutTimer.Stop();
        _clockTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
