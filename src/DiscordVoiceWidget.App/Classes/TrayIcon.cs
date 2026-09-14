using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

internal enum TrayState
{
    Disconnected,
    Connecting,
    Idle,
    InCall,
    InCallMuted,
}

/// <summary>
/// Icone na bandeja do sistema, direto sobre Shell_NotifyIcon.
///
/// Com o widget escondido fora de call, este e o unico sinal de que o app esta
/// rodando - e a unica forma de abrir as configuracoes ou sair. Por isso a cor do
/// icone reflete o estado: cinza desconectado, azul conectado, verde em call,
/// vermelho em call com o microfone mutado.
///
/// Nao usa o NotifyIcon do WinForms: ele puxava o WinForms inteiro para dentro do
/// processo (~17 MB de modulos, mais a inicializacao) so para isto. O icone e
/// desenhado pelo WPF e o menu e um ContextMenu do WPF.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = WM_APP + 1;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreatedMessage;
    private readonly ContextMenu _menu;
    private readonly MenuItem _overlayItem;
    private readonly MenuItem _moveOverlayItem;
    private readonly Dictionary<TrayState, IntPtr> _icons = [];

    private TrayState _state = TrayState.Connecting;
    private string _tooltip = "Discord Voice Widget";
    private bool _added;

    public TrayIcon()
    {
        // Janela de nivel superior, nunca mostrada. Precisa ser de nivel superior (e
        // nao message-only) para receber o broadcast TaskbarCreated quando o explorer
        // reinicia - sem ele o icone some e nao volta.
        _window = new HwndSource(new HwndSourceParameters("DiscordVoiceWidgetTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _window.AddHook(WndProc);

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _overlayItem = MenuItem("Overlay nos jogos", () => OverlayToggleRequested?.Invoke());
        _overlayItem.IsCheckable = false; // a marca reflete o estado real, definido pelo app
        _moveOverlayItem = MenuItem("Mover overlay", () => OverlayMoveRequested?.Invoke());

        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Items.Add(_overlayItem);
        _menu.Items.Add(_moveOverlayItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(MenuItem("Configurações...", () => SettingsRequested?.Invoke()));
        _menu.Items.Add(MenuItem("Reconectar", () => ReconnectRequested?.Invoke()));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(MenuItem("Sair", () => ExitRequested?.Invoke()));

        AddToShell();
    }

    public event Action? SettingsRequested;
    public event Action? ReconnectRequested;
    public event Action? ExitRequested;
    public event Action? OverlayToggleRequested;
    public event Action? OverlayMoveRequested;

    /// <summary>
    /// Resolucao, escala ou configuracao da barra mudou. Esta janela oculta e de nivel
    /// superior e recebe esses broadcasts; os widgets embutidos na barra (janelas filhas)
    /// nao recebem, entao o App repassa a partir daqui.
    /// </summary>
    public event Action? DisplayChanged;

    /// <summary>O explorer reiniciou e recriou as barras de tarefas.</summary>
    public event Action? TaskbarRecreated;

    /// <summary>Atualiza marcas e atalhos dos itens do overlay no menu.</summary>
    public void SetOverlayState(bool enabled, bool moving, string toggleHotkey, string moveHotkey)
    {
        _overlayItem.IsChecked = enabled;
        _overlayItem.InputGestureText = toggleHotkey;

        _moveOverlayItem.IsChecked = moving;
        _moveOverlayItem.Header = moving ? "Fixar overlay aqui" : "Mover overlay";
        _moveOverlayItem.InputGestureText = moveHotkey;
    }

    public void Update(TrayState state, string status)
    {
        var tooltip = $"Discord Voice Widget\n{status}";

        // Cada chamada ao Shell_NotifyIcon e uma mensagem entre processos para o
        // explorer: so envia quando algo mudou de fato.
        if (state == _state && tooltip == _tooltip) return;

        _state = state;
        _tooltip = tooltip;

        var data = NewData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Notificacao do Windows saindo do icone.</summary>
    public void Notify(string title, string text)
    {
        var data = NewData(NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddToShell()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);

        if (!_added)
        {
            FileLog.Write("Shell_NotifyIcon(NIM_ADD) falhou; o icone da bandeja nao sera exibido");
            return;
        }

        // Versao 4: WM_CONTEXTMENU ja chega pronto para o clique direito e o teclado.
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = IconFor(_state),
        szTip = Truncate(_tooltip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // Versao 4: a palavra baixa do lParam e o evento do mouse/teclado.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_CONTEXTMENU:
                    ShowMenu();
                    break;
                case WM_LBUTTONDBLCLK:
                    SettingsRequested?.Invoke();
                    break;
            }

            handled = true;
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // Explorer reiniciou: todos os icones da bandeja foram perdidos.
            AddToShell();
            TaskbarRecreated?.Invoke();
        }
        else if (msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
        {
            DisplayChanged?.Invoke();
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // Sem trazer a janela para frente, o menu nao fecha ao clicar fora dele.
        SetForegroundWindow(_window.Handle);
        _menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private IntPtr IconFor(TrayState state)
    {
        if (_icons.TryGetValue(state, out var cached)) return cached;

        // Desenhado no tamanho real da bandeja: nenhum .ico binario no repositorio e
        // nada de reescalar um bitmap grande, que fica borrado.
        var size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        var png = RenderPng(state, size);

        var handle = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
        _icons[state] = handle;
        return handle;
    }

    private static byte[] RenderPng(TrayState state, int size)
    {
        var s = size / 16.0;
        var white = Brushes.White;
        var stroke = new Pen(white, 1.3 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var radius = (size / 2.0) - 0.5;
            dc.DrawEllipse(new SolidColorBrush(ColorFor(state)), null, new Point(size / 2.0, size / 2.0), radius, radius);

            // Microfone: capsula, arco e haste.
            dc.DrawRoundedRectangle(white, null, new Rect(6.3 * s, 3.2 * s, 3.4 * s, 6 * s), 1.7 * s, 1.7 * s);

            var arc = new PathFigure { StartPoint = new Point(11.6 * s, 8 * s) };
            arc.Segments.Add(new ArcSegment(new Point(4.4 * s, 8 * s), new Size(3.6 * s, 3.2 * s), 0, false, SweepDirection.Clockwise, true));
            dc.DrawGeometry(null, stroke, new PathGeometry([arc]));

            dc.DrawLine(stroke, new Point(8 * s, 11.3 * s), new Point(8 * s, 12.8 * s));

            if (state == TrayState.InCallMuted)
            {
                var slash = new Pen(white, 1.5 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(slash, new Point(4.2 * s, 3.6 * s), new Point(11.8 * s, 12.4 * s));
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Color ColorFor(TrayState state) => state switch
    {
        TrayState.InCall => Color.FromRgb(0x23, 0xA5, 0x5A),
        TrayState.InCallMuted => Color.FromRgb(0xF2, 0x3F, 0x43),
        TrayState.Idle => Color.FromRgb(0x58, 0x65, 0xF2),
        TrayState.Connecting => Color.FromRgb(0x6D, 0x74, 0xA8),
        _ => Color.FromRgb(0x80, 0x84, 0x8E),
    };

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        foreach (var handle in _icons.Values) DestroyIcon(handle);
        _icons.Clear();

        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
