using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeilDesk.App.Export;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Vision;
using VeilDesk.Inference;

namespace ProtectionProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            VerifyStalledInference();
            VerifyOpaqueCover();
            if (args.Length == 2) VerifyRealModels(args[0], args[1]);
            Console.WriteLine("PASS headless protection probe; no desktop windows/capture used");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void VerifyStalledInference()
    {
        using var model = new ControlledModels();
        var settings = new AppSettings();
        settings.Detection.WholeBodyEnabled = true;
        settings.Detection.WholeBodyNudeOnly = false;
        settings.Performance.BodyScanRateHz = 30;
        using var pipeline = new VisionPipeline(new VisionPipelineOptions { ModelDirectory = "." }, model);
        var bounds = new PixelRect(0, 0, 320, 320);
        var start = Environment.TickCount64;
        pipeline.StartProtection(settings, start);
        foreach (var offset in new[] { 100, 1100 })
        {
            using var frame = VideoFrame.Rent(320, 320, 1280, offset, start + offset, "synthetic", bounds);
            frame.Pixels.AsSpan(0, frame.Stride * frame.Height).Clear();
            pipeline.ReportFrame(frame.TimestampMs);
            Require(pipeline.Analyze(frame, settings, frame.TimestampMs).Error is null, "synthetic analysis failed");
        }
        Require(!pipeline.FailClosedActive(settings, start + 1100), "stable analyses should recover");

        model.Block = true;
        using var blockedFrame = VideoFrame.Rent(320, 320, 1280, 3, start + 2000, "synthetic", bounds);
        blockedFrame.Pixels.AsSpan(0, blockedFrame.Stride * blockedFrame.Height).Clear();
        pipeline.ReportFrame(blockedFrame.TimestampMs);
        var inference = Task.Run(() => pipeline.Analyze(blockedFrame, settings, blockedFrame.TimestampMs));
        try
        {
            Require(model.Entered.Wait(2000), "inference did not reach controlled stall");
            var timer = Stopwatch.StartNew();
            var render = Task.Run(() => pipeline.GetRegions(settings, bounds, start + 2700));
            Require(render.Wait(500), "render blocked behind inference lock");
            Require(render.Result.Any(r => r.Kind == CensorRegionKind.FailClosed && r.Box == bounds), "stalled inference must cover all pixels");
            Console.WriteLine($"PASS stalled inference -> cover, decision={timer.Elapsed.TotalMilliseconds:F2} ms (not display latency)");
            pipeline.StopProtection();
            Require(pipeline.GetRegions(settings, bounds, start + 2800).Count == 0, "Stop left regions visible");
        }
        finally { model.Release.Set(); Require(inference.Wait(2000), "inference cleanup timed out"); }
        Require(pipeline.GetRegions(settings, bounds, start + 2900).Count == 0, "late result resurrected stopped masks");
        Console.WriteLine("PASS Stop during inference; late completion cannot recreate masks");
    }

    private static void VerifyOpaqueCover()
    {
        const int width = 320, height = 240;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var monitor = new MonitorDescriptor("synthetic", "synthetic", "synthetic", -1920, -1080, width, height, 1.5, false);
        var settings = new AppSettings();
        settings.Censoring.CornerRadius = 100;
        settings.Censoring.Opacity = .1;
        var guard = CensorComposer.Build(Array.Empty<VeilDesk.Core.Tracking.TrackedDetection>(), settings, monitor.LocalBounds, 0, true);
        var output = ExportService.Composite(source, monitor, guard, settings);
        var rendered = new byte[width * height * 4];
        output.CopyPixels(rendered, width * 4, 0);
        var uncovered = 0;
        for (var i = 0; i < rendered.Length; i += 4)
            if (rendered[i] != 10 || rendered[i + 1] != 4 || rendered[i + 2] != 3 || rendered[i + 3] != 255) uncovered++;
        // Warning text occupies the center; every edge must still be opaque cover.
        foreach (var (x, y) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1), (160, 0) })
        {
            var i = (y * width + x) * 4;
            Require(rendered[i] == 10 && rendered[i + 1] == 4 && rendered[i + 2] == 3 && rendered[i + 3] == 255,
                $"uncovered fail-closed edge at {x},{y}");
        }
        Require(!Enumerable.Range(0, width * height).Any(p => rendered[p * 4 + 3] != 255), "transparent fail-closed pixels");
        Console.WriteLine("PASS compositor: opaque square fail-closed edges with rounded/10%-opacity user style and negative display origin");
    }

    private static void VerifyRealModels(string modelDirectory, string imagePath)
    {
        var decoded = BitmapFrame.Create(new Uri(Path.GetFullPath(imagePath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var bounds = new PixelRect(0, 0, source.PixelWidth, source.PixelHeight);
        var settings = new AppSettings();
        settings.Censoring.FailClosed = false;
        settings.Censoring.CornerRadius = 0;
        settings.Censoring.Opacity = 1;
        settings.Detection.WholeBodyEnabled = true;
        settings.Detection.WholeBodyNudeOnly = false;
        settings.Detection.WholeBodyConfidence = .35;
        settings.Performance.BodyScanRateHz = 30;
        var options = new VisionPipelineOptions { ModelDirectory = Path.GetFullPath(modelDirectory), UseDirectML = false, InferenceThreads = 2 };
        var load = Stopwatch.StartNew();
        using var pipeline = new VisionPipeline(options);
        Console.WriteLine($"models provider={pipeline.Provider} load={load.Elapsed.TotalMilliseconds:F1} ms");
        var start = Environment.TickCount64;
        pipeline.StartProtection(settings, start);
        var measurements = new List<double>();
        VeilDesk.Inference.AnalysisResult? result = null;
        for (var i = 0; i < 10; i++)
        {
            var now = Environment.TickCount64;
            using var frame = VideoFrame.Rent(source.PixelWidth, source.PixelHeight, source.PixelWidth * 4, i + 1, now, "static-image", bounds);
            source.CopyPixels(frame.Pixels, frame.Stride, 0);
            pipeline.ReportFrame(now);
            result = pipeline.Analyze(frame, settings, now);
            Require(result.Error is null, "real ONNX inference failed");
            measurements.Add(result.TotalMs);
            // Exercise the anatomical cadence rather than counting cached detections as new scans.
            Thread.Sleep(140);
        }
        Require(result is not null && result.PersonDetections.Count > 0 && result.NudeDetections.Count > 0, "fixture did not produce actual model detections");
        var regions = result!.Regions.Where(r => r.Kind == CensorRegionKind.Detection).ToList();
        Require(regions.Count > 0, "real anatomical detections produced no censor regions");
        var monitor = new MonitorDescriptor("static", "static", "static", -source.PixelWidth, 0, source.PixelWidth, source.PixelHeight, 1.5, false);
        var composite = ExportService.Composite(source, monitor, regions, settings);
        var before = new byte[source.PixelWidth * source.PixelHeight * 4];
        var after = new byte[before.Length];
        source.CopyPixels(before, source.PixelWidth * 4, 0);
        composite.CopyPixels(after, source.PixelWidth * 4, 0);
        var changed = 0;
        for (var p = 0; p < before.Length; p += 4)
            if (before[p] != after[p] || before[p + 1] != after[p + 1] || before[p + 2] != after[p + 2]) changed++;
        Require(changed > 0, "real detected masks were not baked into compositor output");
        var box = regions[0].Box;
        var center = ((int)box.CenterY * source.PixelWidth + (int)box.CenterX) * 4;
        Require(after[center] == 18 && after[center + 1] == 13 && after[center + 2] == 11, "actual anatomical mask center is not solid censor color");
        measurements.Sort();
        Console.WriteLine($"PASS real static ONNX -> tracker -> anatomical masks -> compositor: people={result.PersonDetections.Count} anatomical={result.NudeDetections.Count} regions={regions.Count} changedPixels={changed}");
        Console.WriteLine($"static full-analysis n=10 p50={measurements[4]:F1} p95={measurements[9]:F1} p99={measurements[9]:F1} ms; not desktop end-to-end latency or accuracy");
    }

    private sealed class ControlledModels : IVisionModelRunner
    {
        public string Provider => "synthetic";
        public string? DirectMLError => null;
        public bool Block;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public float[] RunPerson(float[] input, int size)
        {
            if (Block) { Entered.Set(); if (!Release.Wait(5000)) throw new TimeoutException("controlled stall"); }
            return Output(84, 0);
        }
        public float[] RunNudeNet(float[] input, int size) => Output(22, 3);
        public float[] RunSegmentation(float[] input) => new float[256 * 256];
        private static float[] Output(int channels, int category)
        {
            var output = new float[channels * 2100];
            output[0] = 160; output[2100] = 160; output[4200] = 100; output[6300] = 150;
            output[(4 + category) * 2100] = .95f;
            return output;
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
