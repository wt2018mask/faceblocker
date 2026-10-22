using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeilDesk.App;
using VeilDesk.App.Export;
using VeilDesk.App.Overlay;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Vision;
using VeilDesk.Inference;

namespace RecordingProbe;

internal static class Program
{
    private static string OutputRoot = "";
    [STAThread]
    private static int Main(string[] args)
    {
        OutputRoot = Path.GetFullPath(Path.Combine("artifacts", "recording-probe-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(OutputRoot);
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var exit = 1;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                VerifyMaskCacheBounded();
                VerifyUncertainFaceCover();
                await BlockedStopRegression(dispatcher);
                var pixels = Enumerable.Repeat((byte)200, 320 * 240 * 4).ToArray();
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                var bitmap = BitmapSource.Create(320, 240, 96, 96, PixelFormats.Bgra32, null, pixels, 1280);
                bitmap.Freeze();
                var region = new CensorRegion(1, BodyRegion.FemaleBreastExposed, CensorRegionKind.Detection,
                    new PixelRect(80, 60, 100, 90), .95, null, 0, 0);
                await VerifyExports(bitmap, new[] { region }, dispatcher, "synthetic");
                if (args.Length == 2) await VerifyActualModels(args[0], args[1], dispatcher);
                if (args.Length == 3 && args[2] == "--face")
                    await VerifyActualModels(args[0], args[1], dispatcher, face: true);
                Console.WriteLine("PASS headless recorder/export probe output=" + OutputRoot);
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return exit;
    }

    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static void VerifyUncertainFaceCover()
    {
        var monitor = new MonitorDescriptor("fixture", "fixture", "fixture", 0, 0, 320, 240, 1, false);
        var sourceBytes = Enumerable.Repeat((byte)200, 320 * 240 * 4).ToArray();
        for (var i = 3; i < sourceBytes.Length; i += 4) sourceBytes[i] = 255;
        var source = BitmapSource.Create(320, 240, 96, 96, PixelFormats.Bgra32, null, sourceBytes, 1280);
        var s = Settings("uncertain-face");
        s.Censoring.Style = CensorStyle.Outline;
        s.Censoring.Opacity = 0.1;
        s.Censoring.CornerRadius = 64;
        var region = new CensorRegion(1, BodyRegion.Face, CensorRegionKind.UncertainFace,
            new PixelRect(80, 60, 100, 90), .354, null, 0, 0);
        var output = ExportService.Composite(source, monitor, new[] { region }, s);
        var bytes = new byte[320 * 240 * 4];
        output.CopyPixels(bytes, 1280, 0);
        for (var y = 60; y < 150; y++)
        for (var x = 80; x < 180; x++)
        {
            var i = (y * 320 + x) * 4;
            Check(bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 255,
                "uncertain face cover exposed a pixel");
        }
        Check(bytes[0] == 200, "local face guard covered the whole output");
        Console.WriteLine("PASS uncertain face: every local cover pixel opaque black under Outline/10%/rounded settings; outside unchanged");
    }

    private static void VerifyMaskCacheBounded()
    {
        var monitor = new MonitorDescriptor("fixture", "fixture", "fixture", 0, 0, 320, 240, 1.5, false);
        var window = new OverlayWindow(monitor); // Never shown: no HWND or desktop overlay.
        var settings = Settings("mask-cache");
        settings.Censoring.Style = CensorStyle.Silhouette;
        try
        {
            for (var i = 0; i < 1000; i++)
            {
                var mask = Enumerable.Repeat((byte)255, 16 * 16).ToArray();
                var region = new CensorRegion(1, BodyRegion.Person, CensorRegionKind.WholePerson,
                    new PixelRect(30, 30, 100, 120), .9, mask, 16, 16);
                window.Render(new[] { region }, settings, null);
                Check(window.CachedMaskCount == 1, "segmentation cache retained obsolete frame masks");
            }
            window.Clear();
            Check(window.CachedMaskCount == 0, "Clear retained mask cache");
            Check(!window.IsVisible, "headless cache test opened a window");
            Console.WriteLine("PASS 1000 unseen-window segmentation updates: cache <=1 entry; Clear=0; no visible window");
        }
        finally { window.Close(); }
    }

    private static AppSettings Settings(string label) => new()
    {
        Censoring = { Style = CensorStyle.Solid, CornerRadius = 0, Opacity = 1 },
        Creator = { ExportDirectory = Path.Combine(OutputRoot, label) }
    };

    private static async Task BlockedStopRegression(Dispatcher dispatcher)
    {
        using var source = new BlockingSource();
        var recorder = new VideoRecorder(source, Settings("blocked"), "fixture", false, dispatcher);
        await recorder.StartAsync();
        Check(await Task.Run(() => source.Entered.Wait(2000)), "record loop did not enter source");
        var stop = recorder.StopAsync();
        var second = recorder.StopAsync();
        try
        {
            Check(ReferenceEquals(stop, second), "concurrent Stop returned a premature path");
            await Task.Delay(4300);
            Check(!stop.IsCompleted, "writer finalized underneath a blocked frame operation");
        }
        finally { source.Release.Set(); }
        var path = await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Check(File.ReadAllBytes(path).AsSpan().IndexOf("idx1"u8) >= 0, "AVI was not finalized");
        Console.WriteLine("PASS blocked source >4s: shared Stop task, writer finalized only after loop release");
    }

    private static async Task VerifyExports(BitmapSource bitmap, IReadOnlyList<CensorRegion> regions,
        Dispatcher dispatcher, string label)
    {
        var settings = Settings(label);
        using var source = new StaticSource(bitmap, regions);
        using var capture = source.TryGrabRecordingFrame("fixture")!;
        var screenshot = await ExportService.SaveScreenshotAsync(capture.Frame, source.Monitor, regions,
            settings, true, null, dispatcher);
        var secondScreenshot = await ExportService.SaveScreenshotAsync(capture.Frame, source.Monitor, regions,
            settings, true, null, dispatcher);
        Check(screenshot != secondScreenshot, "default screenshot names collided");
        var png = BitmapFrame.Create(new Uri(screenshot), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        AssertCenter(png, regions[0].Box, bitmap.PixelWidth, bitmap.PixelHeight, 0);
        var recorder = new VideoRecorder(source, settings, "fixture", true, dispatcher);
        await recorder.StartAsync();
        var clock = Stopwatch.StartNew();
        await Task.Delay(1600);
        var stop = recorder.StopAsync();
        Check(ReferenceEquals(stop, recorder.StopAsync()), "Stop tasks differ");
        var path = await stop;
        clock.Stop();
        Check(recorder.SourceFrames == 1, "replayed capture counted as distinct source frames");
        var fps = recorder.MeasuredSourceFps;
        await Task.Delay(100);
        Check(fps == recorder.MeasuredSourceFps, "stopped source FPS kept changing");
        var frames = ReadJpegs(path);
        Check(frames.Count == recorder.FramesWritten && frames.Count > 5, "AVI frame count disagrees with recorder");
        foreach (var jpeg in frames)
        {
            using var stream = new MemoryStream(jpeg);
            var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            AssertCenter(decoded, regions[0].Box, bitmap.PixelWidth, bitmap.PixelHeight, 18);
        }
        var declaredSeconds = frames.Count / 15.0;
        Check(Math.Abs(declaredSeconds - clock.Elapsed.TotalSeconds) < .4, "AVI duration does not match recording time");
        Console.WriteLine($"PASS {label}: PNG + {frames.Count} decoded AVI frames contain mask pixels; uniqueSources=1 duration={declaredSeconds:F3}s wall={clock.Elapsed.TotalSeconds:F3}s fpsFrozen=True");
    }

    private static void AssertCenter(BitmapSource bitmap, PixelRect box, int width, int height, int tolerance)
    {
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        var x = Math.Clamp((int)(box.CenterX * bgra.PixelWidth / width), 0, bgra.PixelWidth - 1);
        var y = Math.Clamp((int)(box.CenterY * bgra.PixelHeight / height), 0, bgra.PixelHeight - 1);
        var index = (y * bgra.PixelWidth + x) * 4;
        Check(Math.Abs(pixels[index] - 18) <= tolerance && Math.Abs(pixels[index + 1] - 13) <= tolerance &&
            Math.Abs(pixels[index + 2] - 11) <= tolerance, "mask center is not baked into exported pixels");
    }

    private static List<byte[]> ReadJpegs(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Check(Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF", "not AVI");
        var frames = new List<byte[]>();
        for (var position = 12; position + 8 <= bytes.Length;)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4)));
            Check(size >= 0 && position + 8L + size <= bytes.Length, "invalid chunk size");
            if (Encoding.ASCII.GetString(bytes, position, 4) == "LIST" && size >= 4 &&
                Encoding.ASCII.GetString(bytes, position + 8, 4) == "movi")
                for (var child = position + 12; child + 8 <= position + 8 + size;)
                {
                    var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(child + 4, 4)));
                    Check(child + 8L + count <= position + 8L + size, "invalid frame chunk");
                    if (Encoding.ASCII.GetString(bytes, child, 4) == "00dc") frames.Add(bytes.AsSpan(child + 8, count).ToArray());
                    child += 8 + count + (count & 1);
                }
            position += 8 + size + (size & 1);
        }
        return frames;
    }

    private static async Task VerifyActualModels(string models, string image, Dispatcher dispatcher, bool face = false)
    {
        var decoded = BitmapFrame.Create(new Uri(Path.GetFullPath(image)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();
        using var source = new StaticSource(bitmap, Array.Empty<CensorRegion>());
        using var capture = source.TryGrabRecordingFrame("fixture")!;
        var settings = Settings("actual-model");
        settings.Censoring.FailClosed = false;
        settings.Detection.Confidence = .2;
        if (face) settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        settings.Performance.UseDirectML = false;
        settings.Performance.InferenceThreads = 2;
        using var pipeline = new VisionPipeline(VisionPipelineOptions.ForSettings(Path.GetFullPath(models), settings));
        var now = Environment.TickCount64;
        pipeline.StartProtection(settings, now);
        pipeline.ReportFrame(capture.Frame.TimestampMs);
        var result = pipeline.Analyze(capture.Frame, settings, now);
        var regions = result.Regions.Where(r => r.Kind == CensorRegionKind.Detection).ToList();
        Check(result.Error is null && regions.Count > 0, "actual model yielded no selected masks");
        Console.WriteLine($"actual model anatomical={result.NudeDetections.Count} faces={result.FaceDetections.Count} mask regions={regions.Count}");
        await VerifyExports(bitmap, regions, dispatcher, face ? "actual-face" : "actual-model");
    }

    private sealed class StaticSource : IRecordingSource, IDisposable
    {
        private readonly VideoFrame _frame;
        private readonly IReadOnlyList<CensorRegion> _regions;
        public MonitorDescriptor Monitor { get; }
        public bool IsRunning => true;
        public StaticSource(BitmapSource bitmap, IReadOnlyList<CensorRegion> regions)
        {
            Monitor = new("fixture", "fixture", "fixture", -bitmap.PixelWidth, 0, bitmap.PixelWidth, bitmap.PixelHeight, 1.5, false);
            _regions = regions;
            _frame = VideoFrame.Rent(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth * 4, 1, Environment.TickCount64, "fixture", Monitor.LocalBounds);
            bitmap.CopyPixels(_frame.Pixels, _frame.Stride, 0);
        }
        public IReadOnlyList<MonitorDescriptor> GetActiveMonitors() => new[] { Monitor };
        public RecordingFrame TryGrabRecordingFrame(string monitorId) => new(_frame.Clone(), Monitor, _regions);
        public void Dispose() => _frame.Dispose();
    }

    private sealed class BlockingSource : IRecordingSource, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool IsRunning => true;
        public IReadOnlyList<MonitorDescriptor> GetActiveMonitors() => new[] { new MonitorDescriptor("fixture", "fixture", "fixture", 0, 0, 320, 240, 1, true) };
        public RecordingFrame? TryGrabRecordingFrame(string monitorId) { Entered.Set(); Release.Wait(); return null; }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
