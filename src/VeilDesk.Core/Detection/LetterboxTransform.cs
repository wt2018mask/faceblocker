using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Detection;

/// <summary>
/// Maps model-input coordinates back to source pixels. Supports the two
/// preprocessing conventions used by the bundled models:
/// NudeNet pads right/bottom to a square; YOLO centers the letterbox.
/// </summary>
public readonly record struct LetterboxTransform(
    int SourceWidth,
    int SourceHeight,
    int ModelWidth,
    int ModelHeight,
    double Scale,
    double PadX,
    double PadY)
{
    public static LetterboxTransform CreateBottomRight(int sourceWidth, int sourceHeight, int modelSize)
    {
        var max = Math.Max(sourceWidth, sourceHeight);
        var scale = max <= 0 ? 1 : (double)modelSize / max;
        return new LetterboxTransform(sourceWidth, sourceHeight, modelSize, modelSize, scale, 0, 0);
    }

    public static LetterboxTransform CreateCentered(int sourceWidth, int sourceHeight, int modelWidth, int modelHeight)
    {
        var scale = Math.Min((double)modelWidth / Math.Max(1, sourceWidth), (double)modelHeight / Math.Max(1, sourceHeight));
        var padX = (modelWidth - sourceWidth * scale) / 2.0;
        var padY = (modelHeight - sourceHeight * scale) / 2.0;
        return new LetterboxTransform(sourceWidth, sourceHeight, modelWidth, modelHeight, scale, padX, padY);
    }

    public PixelRect MapCenterBoxToSource(double cx, double cy, double w, double h)
    {
        if (Scale <= 0) return PixelRect.Empty;
        var x = (cx - PadX) / Scale - w / Scale / 2.0;
        var y = (cy - PadY) / Scale - h / Scale / 2.0;
        var width = w / Scale;
        var height = h / Scale;
        return new PixelRect(x, y, width, height).Clip(new PixelRect(0, 0, SourceWidth, SourceHeight));
    }

    public (double X, double Y) SourceToModel(double x, double y)
        => (x * Scale + PadX, y * Scale + PadY);
}
