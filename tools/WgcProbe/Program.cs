using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WgcProbe;

/// <summary>
/// Minimal Windows Graphics Capture probe. It proves that this machine can:
/// create a D3D11 device, wrap it as a WinRT IDirect3DDevice, capture a monitor,
/// receive a frame, copy it to a staging texture and read the pixels back.
/// Nothing here is shipped in the app; it exists to validate the capture path.
/// </summary>
internal static unsafe class Program
{
    private static readonly ManualResetEventSlim FrameReceived = new(false);

    private static Direct3D11CaptureFramePool? _pool;
    private static GraphicsCaptureSession? _session;
    private static D3D11Context? _d3d;
    private static GraphicsCaptureItem? _item;
    private static int _frameCount;
    private static string? _frameReport;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Native.EnablePerMonitorDpiAwareness();
        Console.WriteLine("WGC probe starting");

        try
        {
            var monitor = Native.GetPrimaryMonitor();
            var info = Native.GetMonitorInfo(monitor);
            Console.WriteLine($"monitor hmon=0x{monitor:X} rect=({info.rcMonitor.Left},{info.rcMonitor.Top})-({info.rcMonitor.Right},{info.rcMonitor.Bottom}) dpi={Native.GetDpiForMonitor(monitor)}");
            Native.LogMonitors();

            _d3d = D3D11Context.Create();
            Console.WriteLine($"d3d11 device=0x{_d3d.Device:X} context=0x{_d3d.Context:X} featureLevel=0x{_d3d.FeatureLevel:X}");

            var inspectable = _d3d.CreateWinRtDevice();
            var graphicsDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            Console.WriteLine($"winrt IDirect3DDevice={graphicsDevice.GetType().FullName}");

            _item = CaptureInterop.CreateItemForMonitor(monitor);
            Console.WriteLine($"capture item '{_item.DisplayName}' size={_item.Size.Width}x{_item.Size.Height}");

            _pool = Direct3D11CaptureFramePool.Create(
                graphicsDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
            Console.WriteLine("capture started, waiting for frame...");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: setup: " + ex);
            return 4;
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!FrameReceived.IsSet && DateTime.UtcNow < deadline)
        {
            while (Native.PeekMessage(out var msg, 0, 0, 0, 1))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Thread.Sleep(10);
        }

        if (!FrameReceived.IsSet)
        {
            Console.WriteLine($"FAIL: no frame arrived (frames={_frameCount})");
            return 5;
        }

        Console.WriteLine("PASS");
        Console.WriteLine(_frameReport);
        return 0;
    }

    private static void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        _frameCount++;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                Console.WriteLine($"frame {_frameCount}: TryGetNextFrame returned null");
                return;
            }

            var desc = _d3d!.Describe(frame.Surface);
            Console.WriteLine($"frame {_frameCount}: surface={desc.Width}x{desc.Height} format={desc.Format} size={frame.ContentSize.Width}x{frame.ContentSize.Height}");

            using var staging = _d3d.CreateStagingTexture(desc);
            _d3d.CopyToStaging(frame.Surface, staging.Texture);

            var pixels = _d3d.ReadPixels(staging, out int rowPitch, out int width, out int height);
            var hash = Convert.ToHexString(SHA256.HashData(pixels));
            long nonBlack = 0;
            int sampleStep = Math.Max(1, (width * height) / 100_000);
            long sampled = 0;
            for (int y = 0; y < height; y += Math.Max(1, 1000 / Math.Max(1, width)))
            {
                var row = pixels.AsSpan(y * rowPitch, width * 4);
                for (int x = 0; x < row.Length; x += 4 * sampleStep)
                {
                    sampled++;
                    if (row[x] > 8 || row[x + 1] > 8 || row[x + 2] > 8) nonBlack++;
                }
            }

            var bmpPath = Path.Combine(AppContext.BaseDirectory, "wgc-probe.bmp");
            Native.WriteBmp(bmpPath, width, height, rowPitch, pixels);
            _frameReport =
                $"captured {width}x{height} rowPitch={rowPitch} bytes={pixels.Length} " +
                $"nonBlack={(sampled == 0 ? 0 : (100.0 * nonBlack / sampled)):F1}% sha256={hash} bmp={bmpPath}";

            FrameReceived.Set();
            _session?.Dispose();
            _pool?.Dispose();
            _pool = null;
            _session = null;
        }
        catch (Exception ex)
        {
            _frameReport = "frame processing failed: " + ex;
            Console.WriteLine(_frameReport);
            FrameReceived.Set();
        }
    }
}

internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, in Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, in Guid iid, out IntPtr result);
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = WindowsCreateString(className, className.Length, out var hstring);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = RoGetActivationFactory(hstring, in InteropIid, out var factoryPtr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                Guid iid = GraphicsCaptureItemIid;
                hr = factory.CreateForMonitor(monitor, in iid, out var itemPtr);
                if (hr != 0)
                {
                    Console.WriteLine($"CreateForMonitor failed 0x{hr:X8} monitor=0x{monitor:X}");
                    Marshal.ThrowExceptionForHR(hr);
                }

                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }
}

internal static class Native
{
    public const int MonitorDefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    public static void EnablePerMonitorDpiAwareness()
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
            Console.WriteLine("DPI awareness was already set or unavailable (this is informational).");
    }

    public static IntPtr GetPrimaryMonitor() => MonitorFromPoint(new Point(), MonitorDefaultToPrimary);

    public static MonitorInfo GetMonitorInfo(IntPtr monitor)
    {
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
            throw new InvalidOperationException("GetMonitorInfo failed: " + Marshal.GetLastWin32Error());
        return info;
    }

    public static uint GetDpiForMonitor(IntPtr monitor)
    {
        // MDT_EFFECTIVE_DPI = 0. Ignore failure (older Windows or unusual monitors).
        return GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 ? x : 96;
    }

    public static void LogMonitors()
    {
        int index = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfoW(monitor, ref info);
            Console.WriteLine($"  display[{index++}] hmon=0x{monitor:X} rect=({info.rcMonitor.Left},{info.rcMonitor.Top})-({info.rcMonitor.Right},{info.rcMonitor.Bottom}) primary={(info.dwFlags & 1) != 0} dpi={GetDpiForMonitor(monitor)}");
            return true;
        }, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    public static void WriteBmp(string path, int width, int height, int rowPitch, byte[] pixels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int imageSize = width * height * 4;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(54 + imageSize);
        w.Write(0);
        w.Write(54);
        w.Write(40);
        w.Write(width);
        w.Write(height);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0);
        w.Write(imageSize);
        w.Write(2835);
        w.Write(2835);
        w.Write(0);
        w.Write(0);
        for (int y = 0; y < height; y++)
            w.Write(pixels.AsSpan(y * rowPitch, width * 4));
    }
}
