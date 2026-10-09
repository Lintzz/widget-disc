using System.Runtime.InteropServices;

namespace DiscordVoiceWidget.App;

/// <summary>
/// P/Invoke usado para posicionar e comportar a janela como um widget de taskbar.
/// Mesmo conjunto que o FluentFlyout usa em Classes/NativeMethods.cs.
/// </summary>
internal static partial class NativeMethods
{
    // ---- estilos -----------------------------------------------------------
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int WS_CHILD = 0x40000000;
    public const int WS_POPUP = unchecked((int)0x80000000);

    /// <summary>A janela nunca recebe foco ao ser clicada.</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Some do Alt+Tab.</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const int WS_EX_TOPMOST = 0x00000008;

    // ---- SetWindowPos ------------------------------------------------------
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT : IEquatable<RECT>
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public readonly bool Equals(RECT other)
            => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override readonly bool Equals(object? obj) => obj is RECT other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    public static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowEx(
        IntPtr hWndParent,
        IntPtr hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    public const uint GW_HWNDNEXT = 2;

    /// <summary>Com hWnd nulo: a janela de nivel superior no topo da ordem Z.</summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetTopWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    /// <summary>
    /// Nome da classe da janela. Buffer na pilha em vez de StringBuilder: roda para
    /// varias janelas a cada reancoragem.
    /// </summary>
    public static unsafe string ClassNameOf(IntPtr hWnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassName(hWnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    // DllImport e nao LibraryImport: o gerador nao marshala delegates de callback.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- monitores ----------------------------------------------------------
    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    // Retornos de GetWindowRgnBox.
    public const int RGN_ERROR = 0;
    public const int NULLREGION = 1;

    /// <summary>
    /// Caixa da regiao de recorte da janela, em coordenadas relativas a propria
    /// janela. RGN_ERROR quando a janela nao tem regiao definida.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial int GetWindowRgnBox(IntPtr hWnd, out RECT lprc);

    // ---- mensagens de janela usadas pelos hooks ---------------------------
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    // ---- eventos de primeiro plano ----------------------------------------
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    /// <summary>
    /// Com WINEVENT_OUTOFCONTEXT o callback chega pela fila de mensagens da thread que
    /// instalou o hook - aqui, a thread de UI - e so quando o evento acontece.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---- overlay: cliques atravessando, cursor e monitores ---------------
    /// <summary>Com WS_EX_LAYERED, o mouse passa direto para a janela de baixo (o jogo).</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    // ---- atalhos globais --------------------------------------------------
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>Segurar a tecla nao dispara o atalho repetidas vezes.</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>Pai de janelas message-only: nunca aparecem e nao recebem broadcasts.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- janela do Discord ------------------------------------------------
    public const uint GW_OWNER = 4;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_MAXIMIZE = 3;
    public const int SW_MINIMIZE = 6;

    /// <summary>Qualquer processo pode trazer a propria janela para frente.</summary>
    public const uint ASFW_ANY = unchecked((uint)-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    public static unsafe string TitleOf(IntPtr hWnd)
    {
        var buffer = stackalloc char[256];
        var length = GetWindowText(hWnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    /// <summary>Restaura uma janela minimizada ao estado anterior (normal ou maximizada).</summary>
    public const int SW_RESTORE = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

    // ---- memoria ----------------------------------------------------------
    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentProcess();

    /// <summary>Com (-1, -1): devolve ao sistema as paginas residentes que nao estao em uso.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSizeEx(IntPtr hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize, uint flags);
}
