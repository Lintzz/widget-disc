using System.Runtime.InteropServices;
using System.Windows.Automation;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Localiza o relogio/data de uma taskbar pelo UI Automation.
///
/// Nas barras secundarias do Windows 11 nao existe TrayNotifyWnd: relogio e botao
/// de notificacoes sao elementos XAML sem HWND proprio. O unico jeito de saber
/// onde eles estao e consultar a arvore de automacao - a mesma tecnica que o
/// FluentFlyout usa (AutomationId "SystemTrayIcon").
///
/// Cada consulta e uma chamada COM entre processos que tambem acorda o explorer,
/// entao ela so acontece quando pedida (<see cref="Refresh"/>): ao o widget
/// aparecer, quando a barra muda e na virada de cada minuto, que e quando o texto
/// da hora pode mudar de largura. Sempre em segundo plano; <see cref="GetCachedBounds"/>
/// so le o ultimo valor.
/// </summary>
internal sealed class TaskbarClockLocator
{
    private const string TrayAutomationId = "SystemTrayIcon";
    private const string ClockClassName = "SystemTray.OmniButtonLeft";

    private readonly object _gate = new();

    private IntPtr _taskbar;
    private AutomationElement? _element;
    private RECT? _bounds;
    private bool _running;
    private bool _again;

    /// <summary>Disparado fora da thread de UI, so quando o retangulo muda.</summary>
    public event Action? BoundsChanged;

    /// <summary>Ultimo retangulo conhecido do relogio desta barra, sem consultar nada.</summary>
    public RECT? GetCachedBounds(IntPtr taskbar)
    {
        lock (_gate)
        {
            if (taskbar == _taskbar) return _bounds;
        }

        // Barra diferente da que esta em cache (troca de monitor, explorer reiniciado).
        Refresh(taskbar);
        return null;
    }

    /// <summary>Esquece tudo: os elementos do explorer antigo nao valem mais.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _taskbar = IntPtr.Zero;
            _element = null;
            _bounds = null;
        }
    }

    /// <summary>Agenda uma consulta em segundo plano. Pedidos durante uma consulta viram uma so a seguir.</summary>
    public void Refresh(IntPtr taskbar)
    {
        lock (_gate)
        {
            if (taskbar != _taskbar)
            {
                _taskbar = taskbar;
                _element = null;
                _bounds = null;
            }

            if (_running)
            {
                _again = true;
                return;
            }

            _running = true;
        }

        _ = Task.Run(Run);
    }

    private void Run()
    {
        while (true)
        {
            IntPtr taskbar;
            AutomationElement? cached;
            lock (_gate)
            {
                taskbar = _taskbar;
                cached = _element;
                _again = false;
            }

            var (element, bounds) = Query(taskbar, cached);

            var changed = false;
            bool again;
            lock (_gate)
            {
                if (taskbar == _taskbar)
                {
                    _element = element;
                    changed = !EqualityComparer<RECT?>.Default.Equals(_bounds, bounds);
                    _bounds = bounds;
                }

                again = _again;
                if (!again) _running = false;
            }

            if (changed) BoundsChanged?.Invoke();
            if (!again) return;
        }
    }

    private static (AutomationElement? Element, RECT? Bounds) Query(IntPtr taskbar, AutomationElement? cached)
    {
        if (taskbar == IntPtr.Zero) return (null, null);

        // Elemento em cache pode ter ficado obsoleto (relogio desligado nas
        // configuracoes): nesse caso refaz a busca na mesma rodada, para o widget
        // nao pular para a posicao de reserva por um instante.
        if (cached is not null && ReadBounds(cached) is { } fromCache) return (cached, fromCache);

        var found = FindClock(taskbar);
        return found is null ? (null, null) : (found, ReadBounds(found));
    }

    private static AutomationElement? FindClock(IntPtr taskbar)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            var candidates = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, TrayAutomationId));

            // Preferencia pelo relogio. Se ele estiver oculto nas configuracoes do
            // Windows, ancora no elemento mais a esquerda da area de sistema (o
            // botao de notificacoes), que ocupa o mesmo canto.
            AutomationElement? leftmost = null;
            var leftmostX = double.MaxValue;

            foreach (AutomationElement candidate in candidates)
            {
                if (candidate.Current.ClassName == ClockClassName) return candidate;

                var x = candidate.Current.BoundingRectangle.Left;
                if (x < leftmostX)
                {
                    leftmost = candidate;
                    leftmostX = x;
                }
            }

            return leftmost;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static RECT? ReadBounds(AutomationElement element)
    {
        try
        {
            // Este processo e PerMonitorV2, entao o UIA ja entrega pixels fisicos.
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0) return null;

            return new RECT
            {
                Left = (int)Math.Round(rect.Left),
                Top = (int)Math.Round(rect.Top),
                Right = (int)Math.Round(rect.Right),
                Bottom = (int)Math.Round(rect.Bottom),
            };
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }
}
