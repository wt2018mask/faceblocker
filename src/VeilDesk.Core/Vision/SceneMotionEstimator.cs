using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Vision;

/// <summary>
/// Measures how the visible content moved between two analysed frames.
///
/// The estimator works on a small grayscale thumbnail and matches the
/// previous thumbnail against the current one with a coarse-to-fine search.
/// It is deliberately simple and deterministic: no optical flow, no external
/// dependency, and a documented fallback when the match is not trustworthy.
///
/// Every shift is compared over the same window, so a shift can never look
/// better simply because it moved the comparison area away from the changed
/// part. A larger displacement therefore always has to be paid for with a
/// worse match.
///
/// The result is used for two things only:
/// * a face track that was not detected again is moved with the content it
///   belongs to, so the mask follows a scrolling feed instead of staying at an
///   abandoned position;
/// * a track whose content left the display is retired at once instead of
///   being held for the whole persistence window.
///
/// The thumbnail never leaves the process and is never persisted.
/// </summary>
public sealed class SceneMotionEstimator
{
    public const int ThumbWidth = 160;
    public const int ThumbHeight = 100;

    /// <summary>Region grid used for the per-region displacement.</summary>
    private const int RegionColumns = 4;
    private const int RegionRows = 3;

    /// <summary>Search radius of the coarsest pyramid level, in its pixels.</summary>
    private const int CoarseRadius = 8;

    /// <summary>Search radius of each refinement level, in its pixels.</summary>
    private const int FineRadius = 4;

    /// <summary>Search radius of the per-region refinement, in thumbnail pixels.</summary>
    private const int RegionRadius = 5;

    /// <summary>
    /// A mean absolute difference above this fraction means the content moved
    /// too much to be matched reliably. The caller then keeps its last
    /// position, which is the conservative fallback.
    /// </summary>
    private const double UnreliableDifference = 0.18;

    /// <summary>
    /// A mean absolute difference above this fraction means the previous
    /// content is gone: the display shows something else now.
    /// </summary>
    private const double ReplacedDifference = 0.45;

    /// <summary>Largest displacement accepted, as a fraction of the display.</summary>
    private const double MaximumDisplacementFraction = 0.60;

    /// <summary>
    /// How much better a match has to be before it is used instead of "no
    /// movement". Reporting motion that is not clearly supported would drag
    /// masks across the display.
    /// </summary>
    private const double RequiredImprovement = 0.90;

    private readonly byte[] _level0 = new byte[ThumbWidth * ThumbHeight];
    private readonly byte[] _level1 = new byte[ThumbWidth / 2 * (ThumbHeight / 2)];
    private readonly byte[] _level2 = new byte[ThumbWidth / 4 * (ThumbHeight / 4)];
    private byte[]? _previous;

    /// <summary>
    /// The mean absolute difference of the last accepted match. It is written
    /// to the audit log so that a rejected motion estimate can be explained.
    /// </summary>
    public double LastDifference { get; private set; }

    /// <summary>Discards the previous frame, e.g. after a capture interruption.</summary>
    public void Reset() => _previous = null;

    /// <summary>
    /// Estimates the motion between the previous analysed frame and
    /// <paramref name="bgra"/>. Returns <see cref="SceneMotion.None"/> when
    /// there is nothing to compare or when the match is not trustworthy, and
    /// <see cref="SceneMotion.Replaced"/> when the previous content is gone.
    /// </summary>
    public SceneMotion Estimate(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || bgra.Length < stride * height) return SceneMotion.None;
        ComputeThumbprint(bgra, width, height, stride);
        Downsample(_level0, ThumbWidth, ThumbHeight, _level1, ThumbWidth / 2, ThumbHeight / 2);
        Downsample(_level1, ThumbWidth / 2, ThumbHeight / 2, _level2, ThumbWidth / 4, ThumbHeight / 4);

        var previous = _previous;
        _previous = (byte[])_level0.Clone();
        if (previous is null) return SceneMotion.None;

        var previous1 = new byte[_level1.Length];
        var previous2 = new byte[_level2.Length];
        Downsample(previous, ThumbWidth, ThumbHeight, previous1, ThumbWidth / 2, ThumbHeight / 2);
        Downsample(previous1, ThumbWidth / 2, ThumbHeight / 2, previous2, ThumbWidth / 4, ThumbHeight / 4);

        // Coarse to fine: the coarsest level finds the rough displacement,
        // every finer level only looks a small distance around it.
        var coarse = FindBest(previous2, _level2, ThumbWidth / 4, ThumbHeight / 4, CoarseRadius, 0, 0);
        if (coarse is null) return SceneMotion.None;
        var mid = FindBest(previous1, _level1, ThumbWidth / 2, ThumbHeight / 2, FineRadius,
            coarse.Value.Dx * 2, coarse.Value.Dy * 2);
        if (mid is null) return SceneMotion.None;
        var fine = FindBest(previous, _level0, ThumbWidth, ThumbHeight, FineRadius,
            mid.Value.Dx * 2, mid.Value.Dy * 2);
        if (fine is null) return SceneMotion.None;

        var difference = fine.Value.Difference;
        LastDifference = difference;
        if (difference >= ReplacedDifference) return SceneMotion.Replaced;
        if (difference >= UnreliableDifference) return SceneMotion.None;

        // The whole display only moves when the match is clearly better than
        // not moving at all.
        var stillCost = WindowCost(previous, _level0, ThumbWidth, ThumbHeight, 0, 0);
        var dx = fine.Value.Dx;
        var dy = fine.Value.Dy;
        if (stillCost < double.MaxValue && difference >= (stillCost / 255.0) * RequiredImprovement)
        {
            dx = 0;
            dy = 0;
        }

        var scaleX = (double)width / ThumbWidth;
        var scaleY = (double)height / ThumbHeight;
        var globalDx = dx * scaleX;
        var globalDy = dy * scaleY;
        if (Math.Abs(globalDx) > width * MaximumDisplacementFraction ||
            Math.Abs(globalDy) > height * MaximumDisplacementFraction)
            return SceneMotion.None;

        var regions = BuildRegions(previous, _level0, scaleX, scaleY, width, height, dx, dy);
        return SceneMotion.Measured(false, globalDx, globalDy, regions);
    }

    private IReadOnlyList<SceneMotionRegion> BuildRegions(byte[] previous, byte[] current,
        double scaleX, double scaleY, int width, int height, int baseDx, int baseDy)
    {
        var regions = new List<SceneMotionRegion>(RegionColumns * RegionRows);
        var cellWidth = ThumbWidth / RegionColumns;
        var cellHeight = ThumbHeight / RegionRows;
        for (var row = 0; row < RegionRows; row++)
        {
            for (var col = 0; col < RegionColumns; col++)
            {
                var x0 = col * cellWidth;
                var y0 = row * cellHeight;
                var match = MatchRegion(previous, current, x0, y0, cellWidth, cellHeight,
                    baseDx, baseDy, RegionRadius);
                var rx = match is null ? baseDx : match.Value.Dx;
                var ry = match is null ? baseDy : match.Value.Dy;
                var confidence = match is null
                    ? 0.0
                    : Math.Clamp(1.0 - match.Value.Difference / UnreliableDifference, 0, 1);
                regions.Add(new SceneMotionRegion(
                    new PixelRect(
                        width * (double)col / RegionColumns,
                        height * (double)row / RegionRows,
                        width / (double)RegionColumns,
                        height / (double)RegionRows),
                    rx * scaleX, ry * scaleY, confidence));
            }
        }
        return regions;
    }

    private MatchResult? MatchRegion(byte[] previous, byte[] current, int x0, int y0,
        int width, int height, int baseDx, int baseDy, int radius)
    {
        var window = RegionWindow(width, height);
        var baseCost = RegionCost(previous, current, ThumbWidth, ThumbHeight, x0, y0,
            window, baseDx, baseDy);
        MatchResult? best = null;
        var bestCost = double.MaxValue;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var cost = RegionCost(previous, current, ThumbWidth, ThumbHeight, x0, y0,
                    window, baseDx + dx, baseDy + dy);
                if (cost < bestCost - 0.0001 ||
                    (cost < bestCost + 0.0001 && best is not null &&
                     Math.Abs(dx) + Math.Abs(dy) <
                     Math.Abs(best.Value.Dx - baseDx) + Math.Abs(best.Value.Dy - baseDy)))
                {
                    bestCost = cost;
                    best = new MatchResult(baseDx + dx, baseDy + dy, cost);
                }
            }
        }
        if (best is null) return null;
        // The region only deviates from the global displacement when the local
        // evidence is clearly better. A region without structure keeps the
        // global motion instead of being dragged by noise.
        if (baseCost < double.MaxValue && best.Value.Difference >= baseCost * RequiredImprovement)
            return new MatchResult(baseDx, baseDy, baseCost / 255.0);
        return new MatchResult(best.Value.Dx, best.Value.Dy, best.Value.Difference / 255.0);
    }

    private readonly record struct Window(int X, int Y, int Width, int Height);

    private static Window RegionWindow(int width, int height) => new(
        width / 6, height / 6, Math.Max(8, width * 3 / 5), Math.Max(6, height * 3 / 5));

    /// <summary>
    /// Searches the displacement that maps the previous thumbnail onto the
    /// current one. All shifts are compared over the same window, so a shift
    /// cannot win by moving the comparison area away from the difference.
    /// </summary>
    private static MatchResult? FindBest(byte[] previous, byte[] current, int width, int height,
        int radius, int centreX, int centreY)
    {
        var window = new Window(width * 2 / 5, height * 2 / 5,
            Math.Max(8, width * 2 / 5), Math.Max(6, height * 2 / 5));
        var still = WindowCost(previous, current, width, height, 0, 0);
        MatchResult? best = null;
        var bestCost = double.MaxValue;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var cost = WindowCost(previous, current, width, height,
                    centreX + dx, centreY + dy);
                if (cost < bestCost - 0.0001 ||
                    (cost < bestCost + 0.0001 && best is not null &&
                     Math.Abs(dx) + Math.Abs(dy) < Math.Abs(best.Value.Dx - centreX) + Math.Abs(best.Value.Dy - centreY)))
                {
                    bestCost = cost;
                    best = new MatchResult(centreX + dx, centreY + dy, cost);
                }
            }
        }
        if (best is null) return null;
        if (still < double.MaxValue && best.Value.Difference >= still * RequiredImprovement)
            return new MatchResult(0, 0, still / 255.0);
        return new MatchResult(best.Value.Dx, best.Value.Dy, best.Value.Difference / 255.0);
    }

    /// <summary>
    /// Mean absolute difference between a fixed window of the previous
    /// thumbnail and the same window of the current thumbnail, moved by
    /// (dx, dy). Returns <see cref="double.MaxValue"/> when the moved window
    /// would leave the thumbnail.
    /// </summary>
    private static double WindowCost(byte[] previous, byte[] current, int width, int height,
        int dx, int dy)
    {
        var window = new Window(width * 2 / 5, height * 2 / 5,
            Math.Max(8, width * 2 / 5), Math.Max(6, height * 2 / 5));
        var x = window.X + dx;
        var y = window.Y + dy;
        if (x < 0 || y < 0 || x + window.Width > width || y + window.Height > height)
            return double.MaxValue;

        long sum = 0;
        var samples = 0;
        for (var wy = 0; wy < window.Height; wy += 2)
        {
            var currentRow = (y + wy) * width;
            var previousRow = (window.Y + wy) * width;
            for (var wx = 0; wx < window.Width; wx += 2)
            {
                sum += Math.Abs(current[currentRow + x + wx] - previous[previousRow + window.X + wx]);
                samples++;
            }
        }
        return samples == 0 ? double.MaxValue : sum / (double)samples;
    }

    private static double RegionCost(byte[] previous, byte[] current, int width, int height,
        int x0, int y0, Window window, int dx, int dy)
    {
        var x = x0 + window.X + dx;
        var y = y0 + window.Y + dy;
        if (x < 0 || y < 0 || x + window.Width > width || y + window.Height > height)
            return double.MaxValue;

        long sum = 0;
        var samples = 0;
        for (var wy = 0; wy < window.Height; wy += 2)
        {
            var currentRow = (y + wy) * width;
            var previousRow = (y0 + window.Y + wy) * width;
            for (var wx = 0; wx < window.Width; wx += 2)
            {
                sum += Math.Abs(current[currentRow + x + wx] - previous[previousRow + x0 + window.X + wx]);
                samples++;
            }
        }
        return samples == 0 ? double.MaxValue : sum / (double)samples;
    }

    private static void Downsample(byte[] source, int sourceWidth, int sourceHeight,
        byte[] destination, int destinationWidth, int destinationHeight)
    {
        for (var y = 0; y < destinationHeight; y++)
        {
            var sy0 = y * 2;
            var sy1 = Math.Min(sourceHeight - 1, sy0 + 1);
            for (var x = 0; x < destinationWidth; x++)
            {
                var sx0 = x * 2;
                var sx1 = Math.Min(sourceWidth - 1, sx0 + 1);
                var sum = source[sy0 * sourceWidth + sx0] + source[sy0 * sourceWidth + sx1] +
                          source[sy1 * sourceWidth + sx0] + source[sy1 * sourceWidth + sx1];
                destination[y * destinationWidth + x] = (byte)(sum >> 2);
            }
        }
    }

    private void ComputeThumbprint(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var stepX = Math.Max(1, width / ThumbWidth);
        var stepY = Math.Max(1, height / ThumbHeight);
        for (var ty = 0; ty < ThumbHeight; ty++)
        {
            var sy = Math.Min(height - 1, ty * stepY + stepY / 2);
            var row = sy * stride;
            for (var tx = 0; tx < ThumbWidth; tx++)
            {
                var sx = Math.Min(width - 1, tx * stepX + stepX / 2);
                var i = row + sx * 4;
                if (i + 2 >= bgra.Length) { _level0[ty * ThumbWidth + tx] = 0; continue; }
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                _level0[ty * ThumbWidth + tx] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
            }
        }
    }

    private readonly record struct MatchResult(int Dx, int Dy, double Difference);
}
