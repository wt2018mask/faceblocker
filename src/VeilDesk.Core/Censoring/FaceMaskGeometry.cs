using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Censoring;

/// <summary>Local face prediction, independent of the temporal hold policy.</summary>
public static class FaceMaskGeometry
{
    public static PixelRect Predict(PixelRect observed, double velocityX, double velocityY,
        int horizonMs, double paddingFraction, PixelRect bounds)
    {
        // Preserve the complete measured face. Prediction is limited in face
        // units, not allowed to sweep across the display during missed frames.
        var lookAhead = Math.Clamp(horizonMs, 0, 500) / 1000.0;
        var dx = Math.Clamp(velocityX * lookAhead, -observed.Width * .25, observed.Width * .25);
        var dy = Math.Clamp(velocityY * lookAhead, -observed.Height * .25, observed.Height * .25);
        var padding = Math.Clamp(paddingFraction, 0, .5);

        // The margin belongs to the observed face. It is applied once, to the
        // face itself, and the local prediction only adds the travel that is
        // still ahead. Growing the swept rectangle instead would apply the
        // margin twice to every pixel of predicted travel and turned a small
        // face into a large rectangle while a page was scrolling.
        var px = observed.Width * padding;
        var py = observed.Height * padding;
        var padded = new PixelRect(observed.X - px, observed.Y - py,
            observed.Width + 2 * px, observed.Height + 2 * py);
        var swept = padded.Offset(dx, dy);
        return padded.Union(swept).Clip(bounds);
    }
}
