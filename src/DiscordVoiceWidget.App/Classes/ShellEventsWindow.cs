using System.Windows.Interop;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Janela oculta que so escuta avisos do sistema.
///
/// Os widgets vivem dentro da barra (janelas filhas) e nao recebem broadcasts de
/// resolucao, configuracao ou reinicio do explorer. Esta janela de nivel superior
/// recebe e o App repassa.
/// </summary>
internal sealed class ShellEventsWindow : IDisposable
{
    private readonly HwndSource _window;
    private readonly uint _taskbarCreatedMessage;

    public ShellEventsWindow()
    {
        // Nunca mostrada. Precisa ser de nivel superior (e nao message-only) para
        // receber o broadcast TaskbarCreated quando o explorer reinicia.
        _window = new HwndSource(new HwndSourceParameters("DiscordVoiceWidgetShellEvents")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _window.AddHook(WndProc);

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Resolucao, escala ou configuracao da barra mudou.</summary>
    public event Action? DisplayChanged;

    /// <summary>O explorer reiniciou e recriou as barras de tarefas.</summary>
    public event Action? TaskbarRecreated;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            TaskbarRecreated?.Invoke();
        }
        else if (msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
        {
            DisplayChanged?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
