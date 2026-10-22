using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeilDesk.Core.Vision;

namespace VeilDesk.App.Overlay;

/// <summary>
/// Converts a captured BGRA frame into a frozen WPF bitmap on a worker thread.
/// Freezing makes it safe to hand to the UI thread without copying again.
/// </summary>
public static class FrameImage
{
    public static BitmapSource ToBitmap(VideoFrame frame)
    {
        var bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Pixels,
            frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
