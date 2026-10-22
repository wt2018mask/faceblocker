using System.IO;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Vision;
using VeilDesk.Inference;

var imagePath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
if (imagePath is null)
{
    Console.WriteLine("usage: VisionProbe <image> [--cpu] [--models <dir>] [--iterations N]");
    return 2;
}
if (!File.Exists(imagePath))
{
    Console.WriteLine("missing image: " + imagePath);
    return 2;
}

var useCpu = args.Contains("--cpu");
var modelsArg = GetArg("--models");
var modelsDir = modelsArg ?? FindModelsDirectory();
if (modelsDir is null || !Directory.Exists(modelsDir))
{
    Console.WriteLine("models directory not found; pass --models <dir>");
    return 2;
}
var iterations = int.TryParse(GetArg("--iterations"), out var n) ? Math.Clamp(n, 1, 20) : 3;

var decoded = BitmapFrame.Create(new Uri(Path.GetFullPath(imagePath)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
int width = converted.PixelWidth, height = converted.PixelHeight;
int stride = width * 4;
var pixels = new byte[stride * height];
converted.CopyPixels(pixels, stride, 0);

var settings = new AppSettings
{
    Detection =
    {
        WholeBodyEnabled = true,
        WholeBodyNudeOnly = false,
        WholeBodyConfidence = 0.35,
        PersonConfidence = 0.35
    },
    Censoring = { FailClosed = false },
    Performance = { UseDirectML = !useCpu }
};
SettingsValidator.ValidateAndClamp(settings);

Console.WriteLine($"image={imagePath} {width}x{height} models={modelsDir} directml={!useCpu}");
VisionPipeline? pipeline = null;
try
{
    var sw = Stopwatch.StartNew();
    pipeline = new VisionPipeline(new VisionPipelineOptions
    {
        ModelDirectory = modelsDir,
        UseDirectML = !useCpu,
        UseHighResolutionPersonModel = width >= 1920,
        InferenceThreads = 2
    });
    Console.WriteLine($"provider={pipeline.Provider} load={sw.Elapsed.TotalMilliseconds:F0} ms dmlError={pipeline.DirectMLError}");

    AnalysisResult? last = null;
    var clock = Stopwatch.StartNew();
    for (var i = 0; i < iterations; i++)
    {
        using var frame = VideoFrame.Rent(width, height, stride, i + 1, clock.ElapsedMilliseconds,
            "probe", new PixelRect(0, 0, width, height));
        Buffer.BlockCopy(pixels, 0, frame.Pixels, 0, pixels.Length);
        last = pipeline.Analyze(frame, settings, clock.ElapsedMilliseconds);
        Console.WriteLine(
            $"iter {i + 1}: total={last.TotalMs:F1}ms pre={last.PreprocessMs:F1} " +
            $"person={last.PersonMs:F1} nude={last.NudeMs:F1} seg={last.SegmentMs:F1} " +
            $"people={last.PersonDetections.Count} body={last.NudeDetections.Count} " +
            $"tracks={last.Tracks.Count} regions={last.Regions.Count} err={last.Error}");
    }

    if (last is null) return 1;
    foreach (var d in last.PersonDetections)
        Console.WriteLine($"  PERSON {d.Confidence:F3} box=({d.Box.X:F0},{d.Box.Y:F0},{d.Box.Width:F0},{d.Box.Height:F0})");
    foreach (var d in last.NudeDetections)
        Console.WriteLine($"  {d.Region.ToNudeNetLabel()} {d.Confidence:F3} box=({d.Box.X:F0},{d.Box.Y:F0},{d.Box.Width:F0},{d.Box.Height:F0})");
    foreach (var r in last.Regions)
        Console.WriteLine($"  REGION {r.Kind}/{r.Source} id={r.TrackId} box=({r.Box.X:F0},{r.Box.Y:F0},{r.Box.Width:F0},{r.Box.Height:F0}) mask={(r.HasMask ? $"{r.MaskWidth}x{r.MaskHeight}" : "none")}");

    foreach (var d in last.PersonDetections) DrawRect(pixels, stride, width, height, d.Box, 0, 0, 255, 4);
    foreach (var d in last.NudeDetections) DrawRect(pixels, stride, width, height, d.Box, 0, 255, 0, 3);

    var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imagePath))!,
        "vision-probe-" + Path.GetFileNameWithoutExtension(imagePath) + ".png");
    SavePng(output, width, height, stride, pixels);
    Console.WriteLine("annotated=" + output);
    return last.Error is null ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine("FAILED: " + ex);
    return 1;
}
finally
{
    pipeline?.Dispose();
}

string? GetArg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string? FindModelsDirectory()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "models");
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "nudenet-320n.onnx")))
            return candidate;
    }
    return null;
}

static void DrawRect(byte[] pixels, int stride, int width, int height, PixelRect box, byte b, byte g, byte r, int thickness)
{
    var left = Math.Clamp((int)Math.Round(box.X), 0, width - 1);
    var top = Math.Clamp((int)Math.Round(box.Y), 0, height - 1);
    var right = Math.Clamp((int)Math.Round(box.Right), 0, width - 1);
    var bottom = Math.Clamp((int)Math.Round(box.Bottom), 0, height - 1);
    for (var t = 0; t < thickness; t++)
    {
        DrawHLine(pixels, stride, width, Math.Clamp(top + t, 0, height - 1), left, right, b, g, r);
        DrawHLine(pixels, stride, width, Math.Clamp(bottom - t, 0, height - 1), left, right, b, g, r);
        DrawVLine(pixels, stride, height, Math.Clamp(left + t, 0, width - 1), top, bottom, b, g, r);
        DrawVLine(pixels, stride, height, Math.Clamp(right - t, 0, width - 1), top, bottom, b, g, r);
    }
}

static void DrawHLine(byte[] pixels, int stride, int width, int y, int x0, int x1, byte b, byte g, byte r)
{
    for (var x = x0; x <= x1; x++)
    {
        var i = y * stride + x * 4;
        pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
    }
}

static void DrawVLine(byte[] pixels, int stride, int height, int x, int y0, int y1, byte b, byte g, byte r)
{
    for (var y = y0; y <= y1; y++)
    {
        var i = y * stride + x * 4;
        pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
    }
}

static void SavePng(string path, int width, int height, int stride, byte[] pixels)
{
    var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(source));
    using var fs = File.Create(path);
    encoder.Save(fs);
}
