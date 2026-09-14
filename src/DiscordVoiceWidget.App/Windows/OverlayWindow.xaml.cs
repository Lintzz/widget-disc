using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>
/// O widget flutuando por cima dos jogos.
///
/// E uma janela comum de outro processo - topmost, transparente e sem foco -, a
/// mesma tecnica dos overlays "fora do processo" do Discord e de apps como o Blitz.
/// Nada e injetado no jogo, entao nao interfere com anti-cheat. O limite dessa
/// tecnica e do proprio Windows: ela aparece sobre jogos em tela cheia sem bordas
/// ou em janela, mas nao sobre tela cheia exclusiva.
///
/// Fora do modo mover a janela deixa todo clique atravessar (WS_EX_TRANSPARENT):
/// jogando, ela nunca rouba um clique. No modo mover ela passa a aceitar o mouse
/// para ser arrastada.
///
/// Visibilidade e escolha do usuario, sem deteccao automatica: ligado (atalho, menu do widget
/// ou configuracoes), aparece enquanto o widget estiver ativo (em call); desligado, a
/// janela nem existe.
///
/// Agendamento: fora de call nada roda. Visivel, a troca de app em primeiro plano chega
/// por WinEvent e reafirma o "sempre por cima" (um jogo ao ganhar foco pode subir), com
/// uma rede de seguranca a cada 6 s. Nao ha varredura de janelas - a posicao e livre.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly TimeSpan SafetyInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PeekDuration = TimeSpan.FromSeconds(2.5);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(200);

    private readonly VoiceWidgetViewModel _viewModel;
    private readonly DispatcherTimer _safetyTimer;
    private readonly DispatcherTimer _peekTimer;

    // Campo: o delegate precisa viver enquanto o hook existir.
    private readonly WinEventProc _foregroundCallback;

    private HwndSource? _source;
    private IntPtr _foregroundHook;

    private OverlayPlacement _placement = new();
    private string _moveHotkeyText = string.Empty;
    private (int X, int Y)? _lastPosition;

    private bool _initialized;
    private bool _active;
    private bool _shown = true;
    private bool _hideWhenNotInCall = true;
    private bool _moving;
    private bool _peeking;

    private bool _dragging;
    private POINT _dragCursorStart;
    private RECT _dragWindowStart;

    public OverlayWindow(VoiceWidgetViewModel viewModel)
    {
        _viewModel = viewModel;
        _foregroundCallback = OnForegroundChanged;

        InitializeComponent();
        DataContext = viewModel;

        _safetyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SafetyInterval };
        _safetyTimer.Tick += (_, _) => ReassertTopmost();

        _peekTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PeekDuration };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            _peeking = false;
            UpdateVisibility();
        };

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => ApplyPlacement();
        Closed += OnClosed;

        PreviewMouseLeftButtonDown += OnDragStart;
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragEnd;
        LostMouseCapture += (_, _) => _dragging = false;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>O usuario terminou de arrastar: a nova posicao deve ser salva.</summary>
    public event Action<OverlayPlacement>? PlacementChanged;

    public event EventHandler<bool>? ActiveChanged;

    public bool IsMoving => _moving;

    public bool HideWhenNotInCall
    {
        get => _hideWhenNotInCall;
        set
        {
            _hideWhenNotInCall = value;
            UpdateActivity();
        }
    }

    /// <summary>Texto do atalho mostrado na dica do modo mover.</summary>
    public string MoveHotkeyText
    {
        get => _moveHotkeyText;
        set
        {
            _moveHotkeyText = value;
            MoveHintText.Text = string.IsNullOrEmpty(value)
                ? "Arraste para posicionar  ·  menu do widget para fixar"
                : $"Arraste para posicionar  ·  {value} para fixar";
        }
    }

    private IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public void SetPlacement(OverlayPlacement placement)
    {
        _placement = new OverlayPlacement { Monitor = placement.Monitor, X = placement.X, Y = placement.Y };
        _lastPosition = null;
        ApplyPlacement();
    }

    // -----------------------------------------------------------------------
    // Modo mover e "espiada"
    // -----------------------------------------------------------------------

    public void BeginMove()
    {
        if (_moving) return;
        _moving = true;

        SetClickThrough(false);
        MoveFrame.Visibility = Visibility.Visible;
        MoveHint.Visibility = Visibility.Visible;
        View.Cursor = Cursors.SizeAll;

        UpdateActivity();
        WindowHelper.SetTopmost(this);
    }

    public void EndMove()
    {
        if (!_moving) return;
        _moving = false;
        _dragging = false;
        ReleaseMouseCapture();

        SetClickThrough(true);
        MoveFrame.Visibility = Visibility.Collapsed;
        MoveHint.Visibility = Visibility.Collapsed;
        View.Cursor = null;

        // Sem jogo aberto o overlay sumiria no mesmo instante; mostrar por um momento
        // confirma onde ele ficou.
        Peek();
        UpdateActivity();
    }

    /// <summary>Mostra o overlay por alguns segundos, mesmo sem jogo em tela cheia.</summary>
    public void Peek()
    {
        _peeking = true;
        _peekTimer.Stop();
        _peekTimer.Start();

        ApplyPlacement();
        UpdateVisibility();
        WindowHelper.SetTopmost(this);
    }

    // -----------------------------------------------------------------------
    // Ciclo de vida e atividade
    // -----------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WndProc);

        WindowHelper.SetNoActivate(this);
        SetClickThrough(!_moving);
        WindowHelper.SetTopmost(this);

        _initialized = true;
        ApplyPlacement();
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        if (!_initialized) return;

        var active = _moving || !HideWhenNotInCall || _viewModel.IsInCall;
        if (active != _active)
        {
            _active = active;

            if (active) StartTracking();
            else StopTracking();

            ActiveChanged?.Invoke(this, active);
        }

        UpdateVisibility();
    }

    private void StartTracking()
    {
        if (_foregroundHook == IntPtr.Zero)
        {
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundCallback,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        _safetyTimer.Start();
    }

    private void StopTracking()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _safetyTimer.Stop();
    }

    private void OnForegroundChanged(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint thread,
        uint time)
    {
        if (_active) ReassertTopmost();
    }

    /// <summary>O jogo ao ganhar foco pode subir por cima: reafirma, mas so se estiver na tela.</summary>
    private void ReassertTopmost()
    {
        if (_shown) WindowHelper.SetTopmost(this);
    }

    private void UpdateVisibility()
    {
        var shouldShow = _moving || _peeking || _active;

        if (shouldShow == _shown) return;
        _shown = shouldShow;

        if (shouldShow) ApplyPlacement();

        Root.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(shouldShow ? 1 : 0, FadeDuration) { EasingFunction = new QuadraticEase() });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VoiceWidgetViewModel.IsInCall)) UpdateActivity();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            // Resolucao ou escala mudou: reaplica a posicao relativa ao monitor.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _lastPosition = null;
                ApplyPlacement();
            });
        }

        return IntPtr.Zero;
    }

    // -----------------------------------------------------------------------
    // Posicao
    // -----------------------------------------------------------------------

    /// <summary>
    /// Leva a janela para a posicao salva, mantendo-a inteira dentro do monitor. O
    /// canto superior esquerdo fica fixo: quando entra gente na call, a pilula cresce
    /// para a direita; perto da borda direita, o recorte a puxa de volta.
    /// </summary>
    private void ApplyPlacement()
    {
        if (!_initialized || _dragging) return;
        if (TaskbarPositionHelper.GetPhysicalSize(this) is not { } size) return;

        var bounds = MonitorLocator.ByDeviceOrPrimary(_placement.Monitor).Bounds;
        var x = bounds.Left + (int)Math.Round(_placement.X * bounds.Width);
        var y = bounds.Top + (int)Math.Round(_placement.Y * bounds.Height);

        x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - size.Width));
        y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - size.Height));

        if (_lastPosition == (x, y)) return;
        _lastPosition = (x, y);

        WindowHelper.SetPosition(this, x, y);
    }

    private void SetClickThrough(bool enabled)
    {
        if (Handle == IntPtr.Zero) return;

        var style = GetWindowLong(Handle, GWL_EXSTYLE);
        style = enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(Handle, GWL_EXSTYLE, style);
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_moving || !GetCursorPos(out _dragCursorStart) || !GetWindowRect(Handle, out _dragWindowStart)) return;

        // Arrasto manual em vez de DragMove(): o DragMove passa pelo loop de movimento do
        // Windows, que ativa a janela e tiraria o foco do jogo.
        _dragging = CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var cursor)) return;

        var x = _dragWindowStart.Left + (cursor.X - _dragCursorStart.X);
        var y = _dragWindowStart.Top + (cursor.Y - _dragCursorStart.Y);
        WindowHelper.SetPosition(this, x, y);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;

        if (!GetWindowRect(Handle, out var rect)) return;

        // Converte para fracao do monitor onde a janela caiu.
        var monitor = MonitorLocator.ForWindow(Handle);
        var bounds = monitor.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        _placement = new OverlayPlacement
        {
            Monitor = monitor.Device,
            X = Math.Clamp((rect.Left - bounds.Left) / (double)bounds.Width, 0, 1),
            Y = Math.Clamp((rect.Top - bounds.Top) / (double)bounds.Height, 0, 1),
        };

        _lastPosition = (rect.Left, rect.Top);
        PlacementChanged?.Invoke(_placement);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopTracking();
        _peekTimer.Stop();
        _source?.RemoveHook(WndProc);
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
