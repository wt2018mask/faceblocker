using System.Runtime.InteropServices;
using VeilDesk.Core.Geometry;

namespace VeilDesk.App.Capture;

/// <summary>
/// Enumerates displays in physical pixels. The process must be per-monitor DPI
/// aware before this runs; otherwise Windows virtualizes the bounds and the
/// overlay would be positioned in scaled coordinates.
/// </summary>
public static class MonitorEnumerator
{
    private const uint MonitorInfoPrimary = 0x1;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DisplayDevice displayDevice, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static void EnablePerMonitorDpiAwareness()
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. WPF also sets awareness via
        // its manifest/first window; doing it before any HWND exists is important
        // so monitor bounds are physical from the first query.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
    }

    public static IReadOnlyList<MonitorDescriptor> Enumerate()
        => EnumerateNative().Select(m => m.Descriptor).ToList();

    /// <summary>
    /// Physical displays with their HMONITOR handles. Handles are only valid
    /// until the display topology changes; use them immediately for capture.
    /// </summary>
    internal static IReadOnlyList<NativeMonitor> EnumerateNative()
    {
        var result = new List<NativeMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info)) return true;

            var deviceName = GetDeviceName(monitor);
            var friendly = "";
            if (!string.IsNullOrEmpty(deviceName))
            {
                var dd = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                if (EnumDisplayDevicesW(deviceName, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString))
                    friendly = dd.DeviceString;
            }

            var dpi = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX : 96;
            var descriptor = new MonitorDescriptor(
                Id: deviceName.Length > 0 ? deviceName : $"HMON-{monitor:X}",
                DeviceName: deviceName,
                FriendlyName: string.IsNullOrWhiteSpace(friendly) ? (deviceName.Length > 0 ? deviceName : "Display") : friendly,
                X: info.Monitor.Left,
                Y: info.Monitor.Top,
                Width: Math.Max(0, info.Monitor.Right - info.Monitor.Left),
                Height: Math.Max(0, info.Monitor.Bottom - info.Monitor.Top),
                DpiScale: dpi / 96.0,
                IsPrimary: (info.Flags & MonitorInfoPrimary) != 0);
            result.Add(new NativeMonitor(descriptor, monitor));
            return true;
        }, IntPtr.Zero);

        return result
            .OrderByDescending(m => m.Descriptor.IsPrimary)
            .ThenBy(m => m.Descriptor.X)
            .ThenBy(m => m.Descriptor.Y)
            .ToList();
    }

    private static string GetDeviceName(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        // MONITORINFOEX has the device name at the end; query it explicitly.
        var ex = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfoExW(monitor, ref ex)) return "";
        return ex.DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoExW(IntPtr monitor, ref MonitorInfoEx info);
}

internal sealed record NativeMonitor(MonitorDescriptor Descriptor, IntPtr Handle);
