using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Vision;

/// <summary>Measured motion of one region of the visible content.</summary>
public sealed record SceneMotionRegion(PixelRect Rect, double Dx, double Dy, double Confidence);

/// <summary>
/// The motion of the visible content between two analysed frames.
///
/// The motion is measured per region because a page does not scroll as one
/// rigid block: a sticky header stays, a feed column moves, and a video keeps
/// playing. A single global vector would drag a stationary face mask across
/// the display, so every track uses the vector of the region it sits in.
/// </summary>
public sealed class SceneMotion
{
    private static readonly SceneMotionRegion[] NoRegions = Array.Empty<SceneMotionRegion>();

    private SceneMotion(bool isReliable, bool contentReplaced, double globalDx, double globalDy,
        IReadOnlyList<SceneMotionRegion> regions)
    {
        IsReliable = isReliable;
        ContentReplaced = contentReplaced;
        GlobalDx = globalDx;
        GlobalDy = globalDy;
        Regions = regions;
    }

    /// <summary>True when the measured displacement can be trusted.</summary>
    public bool IsReliable { get; }

    /// <summary>
    /// True when the previous content is gone: the new frame shares almost
    /// nothing with the previous one. This is the evidence that lets a stale
    /// mask be retired instead of being held for the whole persistence window.
    /// </summary>
    public bool ContentReplaced { get; }

    /// <summary>Displacement of the whole display in physical pixels.</summary>
    public double GlobalDx { get; }

    /// <summary>Displacement of the whole display in physical pixels.</summary>
    public double GlobalDy { get; }

    /// <summary>Per-region displacements in physical pixels.</summary>
    public IReadOnlyList<SceneMotionRegion> Regions { get; }

    /// <summary>No usable motion evidence. Callers keep their last position.</summary>
    public static SceneMotion None { get; } =
        new(false, false, 0, 0, NoRegions);

    /// <summary>The content was replaced; no usable displacement exists.</summary>
    public static SceneMotion Replaced { get; } =
        new(false, true, 0, 0, NoRegions);

    /// <summary>Builds a motion measurement from per-region displacements.</summary>
    public static SceneMotion Measured(bool contentReplaced, double globalDx, double globalDy,
        IReadOnlyList<SceneMotionRegion> regions) =>
        new(!contentReplaced, contentReplaced, globalDx, globalDy, regions);

    /// <summary>
    /// The displacement that applies at a point of the display. Regions are
    /// searched first so that a scrolling column does not drag a stationary
    /// face that sits in a different region.
    /// </summary>
    public (double Dx, double Dy) At(double x, double y)
    {
        if (!IsReliable) return (0, 0);
        for (var i = 0; i < Regions.Count; i++)
        {
            var region = Regions[i];
            if (region.Rect.Contains(x, y)) return (region.Dx, region.Dy);
        }
        return (GlobalDx, GlobalDy);
    }
}
