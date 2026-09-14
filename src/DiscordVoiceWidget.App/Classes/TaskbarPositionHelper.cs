using System.Windows;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>Em qual barra uma janela do widget deve ficar.</summary>
internal enum TaskbarTarget
{
    /// <summary>Barra do monitor principal, ao lado da bandeja.</summary>
    Primary,

    /// <summary>Barra do monitor secundario; recai na principal se nao houver.</summary>
    Secondary,

    /// <summary>
    /// So a barra do monitor secundario, sem recair. Usado quando ha um widget em cada
    /// barra: recair poria dois widgets na principal.
    /// </summary>
    SecondaryOnly,
}

/// <summary>Uma taskbar concreta e o elemento ao lado do qual o widget encosta.</summary>
/// <param name="Handle">Shell_TrayWnd ou Shell_SecondaryTrayWnd.</param>
/// <param name="Bounds">Retangulo da barra, em pixels fisicos.</param>
/// <param name="Anchor">
/// Bandeja (barra principal) ou relogio/data (barra secundaria). Nulo enquanto a
/// localizacao por UI Automation ainda nao respondeu.
/// </param>
/// <param name="IsPrimary">Se e a barra do monitor principal.</param>
internal sealed record TaskbarInfo(IntPtr Handle, RECT Bounds, RECT? Anchor, bool IsPrimary)
{
    public bool IsHorizontal => Bounds.Width >= Bounds.Height;
}

/// <summary>
/// Descobre onde a barra de tarefas esta e calcula a posicao do widget,
/// sempre em pixels fisicos - e o que <see cref="SetWindowPos"/> espera.
///
/// Trabalhar em pixels fisicos evita ter que converter DIP a cada mudanca de
/// DPI: pedimos ao Windows o tamanho real da janela e o movemos na mesma unidade.
/// </summary>
internal static class TaskbarPositionHelper
{
    /// <summary>Folga entre o widget e o que estiver a direita dele.</summary>
    private const int AnchorGap = 8;

    /// <summary>
    /// Reserva usada na barra secundaria enquanto o relogio ainda nao foi localizado:
    /// relogio (~70px) + notificacoes (~24px) + margens. Evita que o widget apareca
    /// por cima da data no primeiro segundo e depois pule de lugar.
    /// </summary>
    private const int SecondaryTrayEstimate = 120;

    /// <summary>
    /// Barra onde o widget deve ficar, ou null se o alvo nao existir agora.
    ///
    /// A secundaria e a preferida: jogos em tela cheia no monitor principal cobrem a
    /// barra principal, e ate janelas topmost somem atras deles. Se ela nao existir
    /// (um monitor so, ou "mostrar a barra em todos os monitores" desligado),
    /// <see cref="TaskbarTarget.Secondary"/> cai para a principal e
    /// <see cref="TaskbarTarget.SecondaryOnly"/> devolve null.
    /// </summary>
    public static TaskbarInfo? GetTaskbar(TaskbarTarget target, TaskbarClockLocator clockLocator)
    {
        if (target != TaskbarTarget.Primary)
        {
            var secondary = FindWindow("Shell_SecondaryTrayWnd", null);
            if (secondary != IntPtr.Zero && IsWindowVisible(secondary) && GetWindowRect(secondary, out var bounds))
            {
                return new TaskbarInfo(secondary, bounds, clockLocator.GetCachedBounds(secondary), IsPrimary: false);
            }

            if (target == TaskbarTarget.SecondaryOnly) return null;
        }

        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary == IntPtr.Zero || !GetWindowRect(primary, out var primaryBounds)) return null;

        // Na principal a bandeja ainda e um HWND classico: sem custo de automacao.
        var tray = FindWindowEx(primary, IntPtr.Zero, "TrayNotifyWnd", null);
        RECT? anchor = tray != IntPtr.Zero && GetWindowRect(tray, out var trayBounds) ? trayBounds : null;

        return new TaskbarInfo(primary, primaryBounds, anchor, IsPrimary: true);
    }

    /// <summary>
    /// Onde colocar uma janela de <paramref name="width"/> x <paramref name="height"/>
    /// pixels fisicos: encostada a esquerda da ancora (bandeja ou relogio),
    /// centralizada na espessura da barra e desviando de outros widgets.
    /// </summary>
    public static (int X, int Y) ComputePosition(
        TaskbarInfo taskbar,
        int width,
        int height,
        IReadOnlyList<RECT>? blockers = null)
    {
        var bar = taskbar.Bounds;
        var reserve = taskbar.IsPrimary ? 0 : SecondaryTrayEstimate;

        if (taskbar.IsHorizontal)
        {
            var anchor = taskbar.Anchor?.Left ?? bar.Right - reserve;
            var x = AvoidBlockers(anchor - AnchorGap - width, width, blockers, r => (r.Left, r.Right));
            var y = bar.Top + ((bar.Height - height) / 2);
            return (Clamp(x, bar.Left, bar.Right - width), Clamp(y, bar.Top, bar.Bottom - height));
        }

        // Barra vertical: centraliza na largura e empilha acima da ancora.
        var bottom = taskbar.Anchor?.Top ?? bar.Bottom - reserve;
        var stackedY = AvoidBlockers(bottom - AnchorGap - height, height, blockers, r => (r.Top, r.Bottom));
        var centeredX = bar.Left + ((bar.Width - width) / 2);
        return (Clamp(centeredX, bar.Left, bar.Right - width), Clamp(stackedY, bar.Top, bar.Bottom - height));
    }

    /// <summary>
    /// Empurra a posicao para tras (esquerda ou para cima) ate nao sobrepor nenhum
    /// outro widget.
    ///
    /// O laco existe porque desviar de um widget pode jogar a janela em cima de um
    /// segundo. O limite de iteracoes so protege contra retangulos patologicos.
    /// </summary>
    private static int AvoidBlockers(
        int start,
        int size,
        IReadOnlyList<RECT>? blockers,
        Func<RECT, (int Start, int End)> axis)
    {
        if (blockers is null || blockers.Count == 0) return start;

        var position = start;
        for (var pass = 0; pass < 8; pass++)
        {
            var moved = false;

            foreach (var blocker in blockers)
            {
                var (blockStart, blockEnd) = axis(blocker);
                if (position + size + AnchorGap <= blockStart || position >= blockEnd + AnchorGap) continue;

                position = blockStart - AnchorGap - size;
                moved = true;
            }

            if (!moved) break;
        }

        return position;
    }

    /// <summary>
    /// Outros widgets ancorados nesta barra, dos quais precisamos desviar.
    ///
    /// Existem dois jeitos de um app se colar na taskbar, e e preciso enxergar os dois:
    ///
    /// 1. Janela topmost solta por cima da barra (o que este widget faz).
    /// 2. Janela reparentada com SetParent dentro da barra. E o caso do FluentFlyout:
    ///    a janela dele ocupa a largura inteira e a parte visivel e recortada com
    ///    SetWindowRgn. EnumWindows nao ve filhas, e o retangulo da janela nao diz
    ///    nada - so a regiao diz onde esta o conteudo, e ela acompanha a largura
    ///    mudando com o nome da musica.
    ///
    /// O filtro de janelas soltas e deliberadamente restritivo. Sem ele entram o
    /// ShellHandwritingCanvas e o jogo em tela cheia (topmost e do tamanho da tela)
    /// e o painel do Vanguard, cujo retangulo vaza para fora do monitor.
    ///
    /// Com o menu Iniciar aberto, o Windows devolve so parte das filhas da barra:
    /// somem este widget e o FluentFlyout, embora continuem visiveis e no lugar. Sem
    /// eles na lista, o widget pulava para cima do vizinho ate o menu fechar. A
    /// propria janela serve de prova: se ela nao apareceu, a enumeracao veio
    /// incompleta e os widgets de <paramref name="lastSeen"/> (a ultima varredura
    /// completa) sao consultados direto pelo handle.
    /// </summary>
    public static List<RECT> GetDockedWidgets(IntPtr self, TaskbarInfo taskbar, List<IntPtr> lastSeen)
    {
        var found = new List<RECT>();
        var handles = new List<IntPtr>();
        var sawSelf = false;
        var bar = taskbar.Bounds;

        GetWindowThreadProcessId(taskbar.Handle, out var explorerPid);

        // Janelas soltas por cima da barra precisam ser topmost, e toda janela topmost
        // fica no topo da ordem Z. Entao basta descer a partir do topo e parar na
        // primeira que nao for topmost, em vez de enumerar todas.
        // Medido nesta maquina: 61 janelas visitadas em vez de 468, 17,7x mais rapido,
        // mesmas candidatas.
        //
        // O limite de passos protege contra a cadeia mudar durante a caminhada (janela
        // destruida ou reordenada no meio): no pior caso a varredura sai incompleta e a
        // proxima, 2 s depois, corrige.
        var steps = 0;
        for (var handle = GetTopWindow(IntPtr.Zero); handle != IntPtr.Zero && steps < 512; handle = GetWindow(handle, GW_HWNDNEXT))
        {
            steps++;
            if ((GetWindowLong(handle, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0) break;

            // Ordem dos filtros: retangulo primeiro, que descarta quase tudo; nome de
            // classe e regiao so para as poucas que cruzam a barra.
            if (handle == self || !GetWindowRect(handle, out var window) || !Intersects(window, bar)) continue;
            if (!IsWindowVisible(handle) || IsShellWindow(ClassNameOf(handle))) continue;

            if (VisibleBounds(handle, window, taskbar) is { } rect)
            {
                found.Add(rect);
                handles.Add(handle);
            }
        }

        // Variavel local em vez de lambda inline: mantem o delegate enraizado durante
        // toda a chamada nativa, fora do alcance do GC.
        EnumWindowsProc embedded = (handle, _) =>
        {
            if (handle == self)
            {
                sawSelf = true;
                return true;
            }

            // As filhas do proprio explorer sao a barra nativa (botoes, relogio).
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == explorerPid || !IsWindowVisible(handle) || !GetWindowRect(handle, out var window)) return true;

            if (VisibleBounds(handle, window, taskbar) is { } rect)
            {
                found.Add(rect);
                handles.Add(handle);
            }
            return true;
        };

        EnumChildWindows(taskbar.Handle, embedded, IntPtr.Zero);

        GC.KeepAlive(embedded);

        if (sawSelf)
        {
            lastSeen.Clear();
            lastSeen.AddRange(handles);
            return found;
        }

        // Enumeracao incompleta: completa com os widgets da ultima varredura boa que
        // ainda existem. VisibleBounds descarta os que sairam desta barra.
        foreach (var handle in lastSeen)
        {
            if (handles.Contains(handle) || !IsWindow(handle) || !IsWindowVisible(handle)) continue;
            if (!GetWindowRect(handle, out var window)) continue;

            if (VisibleBounds(handle, window, taskbar) is { } rect) found.Add(rect);
        }

        return found;
    }

    /// <summary>
    /// Area efetivamente desenhada pela janela, em coordenadas de tela, ou null se
    /// ela nao ocupa um trecho plausivel da barra.
    /// </summary>
    private static RECT? VisibleBounds(IntPtr handle, RECT window, TaskbarInfo taskbar)
    {
        var rect = window;
        var regionKind = GetWindowRgnBox(handle, out var region);

        if (regionKind == NULLREGION) return null;

        if (regionKind != RGN_ERROR)
        {
            // A regiao vem relativa ao canto da janela.
            rect = new RECT
            {
                Left = window.Left + region.Left,
                Top = window.Top + region.Top,
                Right = window.Left + region.Right,
                Bottom = window.Top + region.Bottom,
            };
        }

        var bar = taskbar.Bounds;
        var (length, maxLength, thickness, maxThickness) = taskbar.IsHorizontal
            ? (rect.Width, bar.Width / 3, rect.Height, bar.Height + 8)
            : (rect.Height, bar.Height / 3, rect.Width, bar.Width + 8);

        // Precisa caber na barra, sem vazar para fora dela em nenhuma direcao.
        if (length < 20 || length > maxLength || thickness > maxThickness) return null;
        if (taskbar.IsHorizontal && (rect.Left < bar.Left || rect.Right > bar.Right)) return null;
        if (!taskbar.IsHorizontal && (rect.Top < bar.Top || rect.Bottom > bar.Bottom)) return null;
        if (rect.Right <= bar.Left || rect.Left >= bar.Right) return null;
        if (rect.Bottom <= bar.Top || rect.Top >= bar.Bottom) return null;

        return rect;
    }

    private static bool Intersects(RECT a, RECT b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool IsShellWindow(string className) => className is
        "Shell_TrayWnd" or
        "Shell_SecondaryTrayWnd" or
        "Progman" or
        "WorkerW" or
        "TrayNotifyWnd" or
        "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// Mantem o widget dentro da barra. Sem isso, um widget mais alto que a
    /// taskbar escorrega para fora da tela pela borda de baixo.
    /// </summary>
    private static int Clamp(int value, int min, int max)
    {
        if (max < min) return min;
        return value < min ? min : value > max ? max : value;
    }

    /// <summary>
    /// Tamanho da janela em pixels fisicos, derivado do layout do WPF.
    ///
    /// Nao use GetWindowRect aqui: durante SizeChanged o HWND ainda carrega o
    /// tamanho antigo, e o widget acaba ancorado pela medida anterior - visivel
    /// como um deslocamento a cada pessoa que entra na call. ActualWidth/Height
    /// ja valem o valor novo nesse momento.
    /// </summary>
    public static (int Width, int Height)? GetPhysicalSize(Window window)
    {
        if (PresentationSource.FromVisual(window)?.CompositionTarget is not { } target) return null;

        var toDevice = target.TransformToDevice;
        var width = (int)Math.Ceiling(window.ActualWidth * toDevice.M11);
        var height = (int)Math.Ceiling(window.ActualHeight * toDevice.M22);

        return width > 0 && height > 0 ? (width, height) : null;
    }
}
