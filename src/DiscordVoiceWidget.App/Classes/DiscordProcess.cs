using System.Diagnostics;
using System.IO;
using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Abre e fecha o Discord de desktop. O RPC nao tem comando para nenhum dos dois:
/// abrir passa pelo lancador do proprio Discord, e fechar encerra os processos direto,
/// como o "Sair do Discord" do icone na bandeja.
/// </summary>
internal static class DiscordProcess
{
    /// <summary>Estavel, PTB e Canary: o widget conversa com qualquer um dos tres.</summary>
    private static readonly string[] Names = ["Discord", "DiscordPTB", "DiscordCanary"];

    public static bool IsRunning() => Names.Any(IsRunning);

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>
    /// Inicia o Discord, ou traz para a tela o que ja esta aberto (inclusive escondido na
    /// bandeja). Retorna false se nao achou o Discord instalado.
    ///
    /// Nos dois casos o caminho e o mesmo do atalho do Menu Iniciar: o Update.exe abre o
    /// Discord.exe, e se ja houver um rodando, ele repassa o pedido e o existente mostra a
    /// propria janela. Mostrar a janela por fora (ShowWindow) deixaria o Electron sem
    /// saber que ela voltou.
    /// </summary>
    public static bool Launch()
    {
        var running = Names.FirstOrDefault(IsRunning);

        // O que esta aberto primeiro (PTB e Canary convivem com o estavel); senao, o
        // primeiro instalado.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var flavor = (running is null ? Names : [running, .. Names])
            .FirstOrDefault(name => File.Exists(Path.Combine(localAppData, name, "Update.exe")));

        if (flavor is null) return false;

        // O Discord ja aberto e quem traz a janela para frente; sem esta permissao o
        // Windows so piscaria o botao dele na barra.
        AllowSetForegroundWindow(ASFW_ANY);

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(localAppData, flavor, "Update.exe"),
            Arguments = $"--processStart {flavor}.exe",
            UseShellExecute = false,
        })?.Dispose();

        return true;
    }

    /// <summary>
    /// Janela principal do Discord se estiver aberta (visivel, ainda que minimizada),
    /// senao zero. A bandeja, o atualizador e o overlay tambem sao janelas do processo; a
    /// principal e a maior sem dono com titulo.
    /// </summary>
    public static IntPtr FindMainWindow()
    {
        var pids = new HashSet<uint>();
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) pids.Add((uint)process.Id);
            }
        }

        if (pids.Count == 0) return IntPtr.Zero;

        var best = IntPtr.Zero;
        var bestArea = 0L;

        EnumWindowsProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if (ClassNameOf(hwnd) != "Chrome_WidgetWin_1") return true;

            var title = TitleOf(hwnd);
            if (title.Length == 0 || title.Contains("Updater", StringComparison.OrdinalIgnoreCase)) return true;

            // Tamanho "normal", nao o atual: minimizada a janela vira um quadradinho fora da tela.
            var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref placement)) return true;
            var area = (long)placement.rcNormalPosition.Width * placement.rcNormalPosition.Height;
            if (area > bestArea)
            {
                best = hwnd;
                bestArea = area;
            }
            return true;
        };

        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return best;
    }

    /// <summary>
    /// Leva a janela para o monitor indicado, centralizada na area de trabalho dele e com
    /// o mesmo tamanho (reduzido se nao couber). Maximizada continua maximizada, agora
    /// no monitor novo. Ja estando nele, nao mexe.
    /// </summary>
    public static void MoveToMonitor(IntPtr hwnd, IntPtr monitor)
    {
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == monitor) return;

        var target = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        var primary = new MONITORINFOEX { cbSize = target.cbSize };
        var primaryMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (!GetMonitorInfoEx(monitor, ref target) || !GetMonitorInfoEx(primaryMonitor, ref primary)) return;

        var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement)) return;

        // A posicao "normal" (fora de maximizada) vem em coordenadas da area de trabalho
        // do monitor principal, nao da tela: com a barra embaixo da no mesmo, com ela em
        // cima ou a esquerda, nao.
        var offsetX = primary.rcWork.Left - primary.rcMonitor.Left;
        var offsetY = primary.rcWork.Top - primary.rcMonitor.Top;

        var work = target.rcWork;
        var width = Math.Min(placement.rcNormalPosition.Width, work.Width);
        var height = Math.Min(placement.rcNormalPosition.Height, work.Height);
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (work.Height - height) / 2;

        placement.rcNormalPosition = new RECT
        {
            Left = left - offsetX,
            Top = top - offsetY,
            Right = left - offsetX + width,
            Bottom = top - offsetY + height,
        };

        // Ja maximizada, o Windows guarda a posicao normal nova mas deixa a janela onde
        // esta. Restaurada direto no monitor novo e maximizada de novo, ela vai junto.
        var maximized = placement.showCmd == SW_SHOWMAXIMIZED;
        placement.showCmd = SW_SHOWNORMAL;
        SetWindowPlacement(hwnd, ref placement);
        if (maximized) ShowWindow(hwnd, SW_MAXIMIZE);
    }

    /// <summary>
    /// A janela esta aberta (nao minimizada) neste monitor e e a que o usuario esta vendo:
    /// a ativa, ou a ultima ativa quando o clique no widget passou o foco para a barra.
    /// </summary>
    public static bool IsShownOn(IntPtr hwnd, IntPtr monitor)
    {
        if (IsIconic(hwnd) || MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != monitor) return false;

        var foreground = GetForegroundWindow();
        return foreground == hwnd
            || ClassNameOf(foreground) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>Encerra todos os processos do Discord. Retorna quantos foram encerrados.</summary>
    public static int Quit()
    {
        var killed = 0;

        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        // A arvore inteira: os processos filhos (GPU, audio) morrem junto
                        // com o principal, sem depender da ordem da lista.
                        process.Kill(entireProcessTree: true);
                        killed++;
                    }
                    catch (Exception ex)
                    {
                        // Ja encerrado pela arvore de um irmao, ou sem permissao.
                        FileLog.Write($"fechar Discord ({process.Id}): {ex.Message}");
                    }
                }
            }
        }

        return killed;
    }
}
