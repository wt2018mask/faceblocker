using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeilDesk.App.Export;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;

namespace ExportProbe;

/// <summary>
/// Deterministic check of the shipped screenshot compositor. It feeds a known
/// synthetic frame and a known region through ExportService.Composite, then
/// verifies pixel values inside and outside the region. This proves the export
/// renderer without requiring a live detection. It does not prove live overlay
/// placement; that needs a human-visible desktop and an external camera.
/// </summary>
internal static class Program
{
    private const int Width = 640;
    private const int Height = 360;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var monitor = new MonitorDescriptor(
            @"\\.\DISPLAY1", @"\\.\DISPLAY1", "Synthetic", 0, 0, Width, Height, 1.0, true);

        var source = CreateSource();
        var regionBox = new PixelRect(100, 80, 220, 120);
        var settings = new AppSettings();
        settings.Censoring.Style = CensorStyle.Solid;
        settings.Censoring.Color = "#0B0D12";
        settings.Censoring.Opacity = 1.0;
        settings.Censoring.BorderThickness = 0;

        var region = new CensorRegion(
            1, BodyRegion.Person, CensorRegionKind.Detection, regionBox, 0.99, null, 0, 0);
        BitmapSource composed = ExportService.Composite(source, monitor, new[] { region }, settings);
        BitmapSource control = ExportService.Composite(source, monitor, Array.Empty<CensorRegion>(), settings);

        if (composed.PixelWidth != Width || composed.PixelHeight != Height)
        {
            Console.WriteLine($"FAIL size: {composed.PixelWidth}x{composed.PixelHeight}");
            return 2;
        }

        var composedPixels = Read(composed);
        var controlPixels = Read(control);
        var inside = PixelAt(composedPixels, (int)regionBox.CenterX, (int)regionBox.CenterY);
        var outside = PixelAt(composedPixels, 20, 20);
        var controlInside = PixelAt(controlPixels, (int)regionBox.CenterX, (int)regionBox.CenterY);
        var controlOutside = PixelAt(controlPixels, 20, 20);
        var changed = ChangedFraction(composedPixels, controlPixels);

        Console.WriteLine($"source center bgr={controlInside.B},{controlInside.G},{controlInside.R}");
        Console.WriteLine($"composed center bgr={inside.B},{inside.G},{inside.R}");
        Console.WriteLine($"composed corner bgr={outside.B},{outside.G},{outside.R}");
        Console.WriteLine($"control outside bgr={controlOutside.B},{controlOutside.G},{controlOutside.R}");
        Console.WriteLine($"changed pixels within region box: {changed:P2}");

        var path = Path.Combine(AppContext.BaseDirectory, "export-probe.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composed));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine($"png={path}");

        // The solid fill is #0B0D12 at full opacity. Allow +/- 2 for WPF's
        // Pbgra32 rounding. Outside the region the original frame must survive.
        var insideOk = Near(inside, 0x12, 0x0D, 0x0B, 2);
        var outsideOk = Near(outside, controlOutside.B, controlOutside.G, controlOutside.R, 2);
        var unchangedControlOk = Near(controlInside, controlOutside.B, controlOutside.G, controlOutside.R, 0);
        var coverageOk = changed > 0.08 && changed < 0.15;

        Console.WriteLine($"inside-ok={insideOk} outside-ok={outsideOk} control-unchanged={unchangedControlOk} coverage-ok={coverageOk}");
        if (insideOk && outsideOk && unchangedControlOk && coverageOk)
        {
            Console.WriteLine("PASS screenshot compositor");
            return 0;
        }

        Console.WriteLine("FAIL screenshot compositor");
        return 3;
    }

    private static BitmapSource CreateSource()
    {
        var stride = Width * 4;
        var pixels = new byte[stride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 200;      // B
                pixels[i + 1] = 120;  // G
                pixels[i + 2] = 40;   // R
                pixels[i + 3] = 255;  // A
            }
        }

        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Read(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static Pixel PixelAt(byte[] pixels, int x, int y)
    {
        var i = (y * Width + x) * 4;
        return new Pixel(pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    private static bool Near(Pixel pixel, byte b, byte g, byte r, int tolerance)
        => Math.Abs(pixel.B - b) <= tolerance &&
           Math.Abs(pixel.G - g) <= tolerance &&
           Math.Abs(pixel.R - r) <= tolerance;

    private static double ChangedFraction(byte[] composed, byte[] control)
    {
        var changed = 0;
        for (var i = 0; i < composed.Length; i += 4)
        {
            if (composed[i] != control[i] || composed[i + 1] != control[i + 1] || composed[i + 2] != control[i + 2])
                changed++;
        }
        return changed / (double)(Width * Height);
    }

    private readonly record struct Pixel(byte B, byte G, byte R);
}
