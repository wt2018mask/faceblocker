using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeilDesk.App.Overlay;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Vision;

namespace VeilDesk.App.Export;

/// <summary>
/// Renders screenshots with the same censor visuals used on screen. The frame
/// is captured before the overlay, then regions are composited on top, so the
/// exported image contains exactly what a viewer would consider hidden while
/// never looking at the desktop through a second capture path.
/// </summary>
public static class ExportService
{
    public static async Task<string> SaveScreenshotAsync(
        VideoFrame frame,
        MonitorDescriptor monitor,
        IReadOnlyList<CensorRegion> regions,
        AppSettings settings,
        bool includeCensorship,
        string? path,
        Dispatcher dispatcher)
    {
        var directory = string.IsNullOrWhiteSpace(settings.Creator.ExportDirectory)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VeilDesk")
            : settings.Creator.ExportDirectory;
        directory = Environment.ExpandEnvironmentVariables(directory);
        Directory.CreateDirectory(directory);
        path ??= System.IO.Path.Combine(directory,
            $"VeilDesk-{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(monitor.FriendlyName)}.png");

        BitmapSource snapshot = FrameImage.ToBitmap(frame);
        BitmapSource output = await dispatcher.InvokeAsync(() =>
            includeCensorship
                ? Composite(snapshot, monitor, regions, settings)
                : snapshot);

        await Task.Run(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            using var stream = File.Create(path);
            encoder.Save(stream);
        });
        return path;
    }

    public static BitmapSource Composite(BitmapSource snapshot, MonitorDescriptor monitor,
        IReadOnlyList<CensorRegion> regions, AppSettings settings,
        int? maxWidth = null, int? maxHeight = null)
    {
        var width = snapshot.PixelWidth;
        var height = snapshot.PixelHeight;
        var scale = 1.0;
        if (maxWidth is > 0 && width > maxWidth.Value) scale = Math.Min(scale, maxWidth.Value / (double)width);
        if (maxHeight is > 0 && height > maxHeight.Value) scale = Math.Min(scale, maxHeight.Value / (double)height);
        var outputWidth = Math.Max(2, (int)Math.Round(width * scale / 2) * 2);
        var outputHeight = Math.Max(2, (int)Math.Round(height * scale / 2) * 2);
        var scaleX = outputWidth / (double)width;
        var scaleY = outputHeight / (double)height;

        var root = new Grid
        {
            Width = outputWidth,
            Height = outputHeight,
            ClipToBounds = true,
            Background = Brushes.Black
        };
        root.Children.Add(new Image
        {
            Source = snapshot,
            Width = outputWidth,
            Height = outputHeight,
            Stretch = Stretch.Fill
        });

        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            RenderTransform = new ScaleTransform(scaleX, scaleY),
            RenderTransformOrigin = new Point(0, 0)
        };
        var cache = new Dictionary<byte[], BitmapSource>(ReferenceEqualityComparer.Instance);
        ImageSource? custom = null;
        if (settings.Censoring.Style == CensorStyle.Image &&
            !string.IsNullOrWhiteSpace(settings.Censoring.ImagePath) &&
            File.Exists(settings.Censoring.ImagePath))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(settings.Censoring.ImagePath, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            custom = image;
        }

        var failClosed = regions.Any(r => r.Kind == CensorRegionKind.FailClosed);
        foreach (var region in regions)
        {
            if (failClosed && region.Kind != CensorRegionKind.FailClosed) continue;
            var visual = new CensorVisual();
            visual.Apply(region, settings, snapshot, custom, 1.0, monitor, cache);
            canvas.Children.Add(visual);
        }
        root.Children.Add(canvas);

        root.Measure(new Size(outputWidth, outputHeight));
        root.Arrange(new Rect(0, 0, outputWidth, outputHeight));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var result = new string(chars).Trim('-');
        return string.IsNullOrEmpty(result) ? "display" : result;
    }
}
