using static DiscordVoiceWidget.App.NativeMethods;

namespace DiscordVoiceWidget.App;

/// <summary>Um monitor: handle, nome de dispositivo (\\.\DISPLAY1) e retangulo em pixels fisicos.</summary>
internal readonly record struct MonitorInfo(IntPtr Handle, string Device, RECT Bounds);

internal static class MonitorLocator
{
    public static MonitorInfo Primary()
        => Describe(MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY));

    public static MonitorInfo ForWindow(IntPtr hwnd)
        => Describe(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    /// <summary>
    /// Monitor pelo nome de dispositivo, ou o principal se ele nao existir mais.
    /// O numero do DISPLAY pode mudar (monitor desconectado, driver atualizado); cair
    /// no principal e o comportamento mais util nesses casos.
    /// </summary>
    public static MonitorInfo ByDeviceOrPrimary(string? device)
    {
        if (string.IsNullOrEmpty(device)) return Primary();

        MonitorInfo? match = null;
        MonitorEnumProc callback = (handle, _, _, _) =>
        {
            var info = Describe(handle);
            if (!string.Equals(info.Device, device, StringComparison.OrdinalIgnoreCase)) return true;

            match = info;
            return false;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return match ?? Primary();
    }

    private static MonitorInfo Describe(IntPtr handle)
    {
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoEx(handle, ref info)
            ? new MonitorInfo(handle, info.szDevice, info.rcMonitor)
            : new MonitorInfo(handle, string.Empty, default);
    }
}
