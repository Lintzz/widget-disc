using System.Windows;
using System.Windows.Interop;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Comportamento de janela que o WPF nao expoe. Portado do FluentFlyout
/// (FluentFlyoutWPF/Classes/WindowHelper.cs).
/// </summary>
internal static class WindowHelper
{
    /// <summary>
    /// Impede que a janela receba foco e a tira do Alt+Tab.
    ///
    /// Precisa rodar depois que o handle existe (SourceInitialized). Sem
    /// WS_EX_NOACTIVATE, clicar no widget rouba o foco do app em primeiro plano -
    /// o suficiente para tirar alguem de um jogo em tela cheia.
    /// </summary>
    public static void SetNoActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Reafirma o z-order de topo.
    ///
    /// O Windows rebaixa janelas topmost silenciosamente quando outro app entra em
    /// primeiro plano exclusivo, entao isso precisa ser chamado periodicamente -
    /// mesma solucao do FluentFlyout.
    /// </summary>
    public static void SetTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// A janela em primeiro plano cobre o monitor inteiro (jogo em tela cheia, F11 no
    /// navegador). A area de trabalho e a barra de tarefas nao contam.
    /// </summary>
    public static bool IsForegroundFullscreen()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !GetWindowRect(foreground, out var rect)) return false;

        var shellClass = ClassNameOf(foreground);
        if (shellClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        var bounds = MonitorLocator.ForWindow(foreground).Bounds;
        return bounds.Width > 0
               && rect.Left <= bounds.Left && rect.Top <= bounds.Top
               && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
    }

    /// <summary>Move a janela usando pixels fisicos, sem ativar nem redimensionar.</summary>
    public static void SetPosition(Window window, int x, int y)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
