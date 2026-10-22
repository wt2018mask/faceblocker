using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VeilDesk.App.Capture;
using VeilDesk.Core.Vision;

namespace CaptureProbe;

/// <summary>
/// Runtime probe for the shipped CaptureService. Starts WGC on every display,
/// waits for real frames, prints measured status, and saves the freshest frame
/// as a BMP. No UI is involved, so failures here are capture-stack failures.
/// </summary>
internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        MonitorEnumerator.EnablePerMonitorDpiAwareness();
        var monitors = MonitorEnumerator.Enumerate();
        Console.WriteLine($"displays={monitors.Count}");
        foreach (var monitor in monitors)
            Console.WriteLine($"  {monitor.Id} '{monitor.FriendlyName}' {monitor.Width}x{monitor.Height} @ {monitor.DpiScale:0.##}x ({monitor.X},{monitor.Y}) primary={monitor.IsPrimary}");

        await using var capture = new CaptureService();
        var sw = Stopwatch.StartNew();
        try
        {
            await capture.StartAsync(monitors, new CaptureOptions(FramesPerSecond: 15, CaptureCursor: false));
            Console.WriteLine($"capture started in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL start: " + ex);
            return 2;
        }

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var status = capture.GetStatus();
            foreach (var s in status)
                Console.WriteLine($"  t={sw.ElapsedMilliseconds,5}ms {s.MonitorId} frames={s.FramesDelivered} dropped={s.FramesDropped} fps={s.MeasuredFps:F1} size={s.Width}x{s.Height} last={s.LastFrameUtc:HH:mm:ss.fff} err={s.Error ?? "-"}");
            if (status.Count > 0 && status.All(s => s.FramesDelivered >= 3)) break;
        }

        var statusFinal = capture.GetStatus();
        var failed = statusFinal.Count == 0 || statusFinal.All(s => s.FramesDelivered == 0);
        if (failed)
        {
            Console.WriteLine("FAIL: no frames were delivered");
            await capture.ShutdownAsync();
            return 3;
        }

        foreach (var status in statusFinal)
        {
            Report(capture, status);
        }

        await capture.ShutdownAsync();
        Console.WriteLine($"stopped in {sw.ElapsedMilliseconds} ms total");
        return 0;
    }

    private static void Report(CaptureService capture, CaptureStatus status)
    {
        using var frame = capture.TryGrabLatestFrame(status.MonitorId);
        if (frame is null)
        {
            Console.WriteLine($"FAIL: no retained frame for {status.MonitorId}");
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(frame.Span));
        var path = Path.Combine(AppContext.BaseDirectory, $"capture-{Sanitize(status.MonitorId)}.bmp");
        WriteBmp(path, frame);
        var pngPath = Path.Combine(AppContext.BaseDirectory, $"capture-{Sanitize(status.MonitorId)}.png");
        WritePng(pngPath, frame);
        long nonBlack = 0, sampled = 0;
        var pixels = frame.Pixels;
        for (var y = 0; y < frame.Height; y += 37)
        {
            var row = pixels.AsSpan(y * frame.Stride, frame.Width * 4);
            for (var x = 0; x < row.Length; x += 4 * 41)
            {
                sampled++;
                if (row[x] > 8 || row[x + 1] > 8 || row[x + 2] > 8) nonBlack++;
            }
        }
        Console.WriteLine($"PASS {status.MonitorId}: {frame.Width}x{frame.Height} stride={frame.Stride} bytes={frame.Stride * frame.Height} sha256={hash} nonBlack={(sampled == 0 ? 0 : 100.0 * nonBlack / sampled):F1}% bmp={path} png={pngPath}");
    }

    private static void WritePng(string path, VideoFrame frame)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            frame.Width, frame.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null,
            frame.Pixels, frame.Stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static void WriteBmp(string path, VideoFrame frame)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var imageSize = frame.Width * frame.Height * 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + imageSize);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(frame.Width);
        writer.Write(frame.Height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (var y = 0; y < frame.Height; y++)
            writer.Write(frame.Span.Slice(y * frame.Stride, frame.Width * 4));
    }
}
