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
        var sweep = observed.Union(observed.Offset(dx, dy));
        var padding = Math.Clamp(paddingFraction, 0, .5);
        var px = observed.Width * padding;
        var py = observed.Height * padding;
        return new PixelRect(sweep.X - px, sweep.Y - py,
            sweep.Width + 2 * px, sweep.Height + 2 * py).Clip(bounds);
    }
}
