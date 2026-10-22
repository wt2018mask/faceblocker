using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using VeilDesk.Core.Vision;
using DetectionRecord = VeilDesk.Core.Detection.Detection;

namespace VeilDesk.Core.Tests;

public class ScrollTrackingRegressionTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1280, 720);

    private static DetectionRecord Face(PixelRect box, double confidence = 0.9) =>
        new(BodyRegion.Face, confidence, box);

    private static SceneMotion Motion(double dx, double dy, bool reliable = true)
    {
        var regions = new List<SceneMotionRegion>
        {
            new(new PixelRect(0, 0, Monitor.Width, Monitor.Height), dx, dy, 1.0),
        };
        return reliable
            ? SceneMotion.Measured(false, dx, dy, regions)
            : SceneMotion.None;
    }

    /// <summary>
    /// A page-like surface: a slow ramp plus many distinct blocks. The blocks
    /// make the match unambiguous, the ramp keeps the surface resolvable on
    /// the estimator's small thumbnail.
    /// </summary>
    private static byte[] Frame(int width, int height, int offsetY, int? middleBandOffset = null)
    {
        var pixels = new byte[width * height * 4];
        var band = height / 3;
        var markers = new List<(int X, int Y)>();
        for (var i = 0; i < 48; i++)
        {
            var mx = (int)(Fraction(i * 12.9898 + 3) * (width - 90));
            var my = (int)(Fraction(i * 78.233 + 5) * (height - 110));
            markers.Add((mx, my));
        }
        for (var y = 0; y < height; y++)
        {
            var inMiddle = middleBandOffset.HasValue && y >= band && y < 2 * band;
            var source = y + (inMiddle ? middleBandOffset.Value : offsetY);
            for (var x = 0; x < width; x++)
            {
                var value = 40 + 60.0 * (y / (double)height);
                foreach (var (mx, my) in markers)
                {
                    var offset = source - my;
                    if (offset < 0 || offset >= 80) continue;
                    if (x < mx || x >= mx + 60) continue;
                    value += 170 * (1.0 - offset / 80.0);
                }
                var level = (byte)Math.Clamp(value, 0, 255);
                var i = (y * width + x) * 4;
                pixels[i] = level;
                pixels[i + 1] = level;
                pixels[i + 2] = level;
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    private static double Fraction(double seed)
    {
        var v = Math.Sin(seed) * 43758.5453;
        return v - Math.Floor(v);
    }

    [Fact]
    public void MissedFaceFollowsTheMeasuredContentMotion()
    {
        var tracker = new SubjectTracker();
        var face = new PixelRect(500, 400, 100, 120);
        tracker.Update(new[] { Face(face) }, 1000, Monitor);
        // The page scrolled upwards by 60 pixels while the face was not
        // detected again.
        var tracks = tracker.Update(Array.Empty<DetectionRecord>(), 1100, Monitor, Motion(0, -60));

        var track = Assert.Single(tracks);
        Assert.Equal(face.Y - 60, track.CompensatedBox.Y, 1);
    }

    [Fact]
    public void AFaceThatLeftTheViewportIsRetiredWithoutWaitingForTheHoldWindow()
    {
        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        var tracker = new SubjectTracker();
        var face = new PixelRect(500, 640, 100, 120);
        tracker.Update(new[] { Face(face) }, 1000, Monitor);
        Assert.NotEmpty(CensorComposer.Build(tracker.Snapshot(), settings, Monitor, 1100, false));

        // The content scrolls up by 200 pixels per analysed frame until the
        // face has left the display.
        IReadOnlyList<TrackedDetection> tracks = Array.Empty<TrackedDetection>();
        for (var step = 1; step <= 10; step++)
        {
            tracks = tracker.Update(Array.Empty<DetectionRecord>(), 1000 + step * 80, Monitor, Motion(0, -200));
            if (tracks.Count == 0) break;
        }
        Assert.Empty(tracks);
        Assert.Empty(CensorComposer.Build(tracks, settings, Monitor, 1800, false));
    }

    [Fact]
    public void GhostMaskIsGoneWellInsideTheProvisionalTarget()
    {
        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        var tracker = new SubjectTracker();
        var face = new PixelRect(500, 640, 100, 120);
        tracker.Update(new[] { Face(face) }, 1000, Monitor);

        var departureMs = -1;
        for (var step = 1; step <= 12; step++)
        {
            var now = 1000 + step * 40;
            var tracks = tracker.Update(Array.Empty<DetectionRecord>(), now, Monitor, Motion(0, -200));
            var masks = CensorComposer.Build(tracks, settings, Monitor, now, false);
            if (masks.Count == 0)
            {
                departureMs = now - 1000;
                break;
            }
        }
        Assert.True(departureMs >= 0, "the mask never disappeared");
        Assert.True(departureMs < 500, $"the mask survived {departureMs} ms after the content departed");
    }

    [Fact]
    public void UnreliableMotionKeepsTheConservativeFallback()
    {
        var tracker = new SubjectTracker();
        var face = new PixelRect(500, 400, 100, 120);
        tracker.Update(new[] { Face(face) }, 1000, Monitor);
        var tracks = tracker.Update(Array.Empty<DetectionRecord>(), 1100, Monitor, Motion(0, -60, reliable: false));
        var track = Assert.Single(tracks);
        Assert.Equal(face.Y, track.CompensatedBox.Y, 1);
    }

    [Fact]
    public void AStationaryFaceIsNotDraggedByMotionInAnotherRegion()
    {
        var estimator = new SceneMotionEstimator();
        var width = 1280;
        var height = 720;
        var first = Frame(width, height, 0);
        var second = Frame(width, height, 0, middleBandOffset: 24);

        estimator.Estimate(first, width, height, width * 4);
        var motion = estimator.Estimate(second, width, height, width * 4);
        Assert.True(motion.IsReliable,
            $"the banded scroll was not matched (difference {estimator.LastDifference:0.000})");

        var still = motion.At(width * 0.5, height * 0.15);
        var scrolling = motion.At(width * 0.5, height * 0.5);
        Assert.True(Math.Abs(still.Dy) < 10,
            $"the still region should not move, measured {still.Dy:0.#}");
        Assert.True(Math.Abs(scrolling.Dy + 24) < 12,
            $"the scrolling region should move by about -24 pixels, measured {scrolling.Dy:0.#}");
    }

    [Fact]
    public void VerticalScrollIsMeasuredWithTheRightSign()
    {
        var estimator = new SceneMotionEstimator();
        var width = 1280;
        var height = 720;
        var first = Frame(width, height, 0);
        var second = Frame(width, height, 40);

        estimator.Estimate(first, width, height, width * 4);
        var motion = estimator.Estimate(second, width, height, width * 4);
        Assert.True(motion.IsReliable,
            $"the scroll was not matched (difference {estimator.LastDifference:0.000})");
        Assert.True(Math.Abs(motion.GlobalDy + 40) < 14,
            $"expected about -40 pixels of vertical travel, measured {motion.GlobalDy:0.#}");
    }

    [Fact]
    public void ReplacedContentKeepsTheHoldPolicyForATemporaryDetectionGap()
    {
        // A detection gap and a new page look the same to a motion estimator.
        // The hold window exists to cover such a gap, so a replaced scene must
        // not delete a track that is still within its persistence.
        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        var tracker = new SubjectTracker();
        tracker.Update(new[] { Face(new PixelRect(500, 400, 100, 120)) }, 1000, Monitor);
        var tracks = tracker.Update(Array.Empty<DetectionRecord>(), 1100, Monitor, SceneMotion.Replaced);
        var track = Assert.Single(tracks);
        Assert.Equal(1, track.Missed);
        Assert.NotEmpty(CensorComposer.Build(tracks, settings, Monitor, 1100, false));
    }

    [Fact]
    public void AReversingScrollMovesTheMaskBack()
    {
        var tracker = new SubjectTracker();
        var face = new PixelRect(500, 400, 100, 120);
        tracker.Update(new[] { Face(face) }, 1000, Monitor);
        tracker.Update(Array.Empty<DetectionRecord>(), 1100, Monitor, Motion(0, -60));
        var tracks = tracker.Update(Array.Empty<DetectionRecord>(), 1200, Monitor, Motion(0, 40));
        var track = Assert.Single(tracks);
        Assert.Equal(face.Y - 20, track.CompensatedBox.Y, 1);
    }

    [Fact]
    public void ScrollingFaceThatIsDetectedAgainKeepsOneTrack()
    {
        var tracker = new SubjectTracker();
        tracker.Update(new[] { Face(new PixelRect(500, 400, 100, 120)) }, 1000, Monitor);
        var tracks = tracker.Update(new[] { Face(new PixelRect(500, 340, 100, 120)) }, 1100, Monitor, Motion(0, -60));
        var track = Assert.Single(tracks);
        Assert.Equal(0, track.Missed);
        Assert.Equal(340, track.CompensatedBox.Y, 1);
    }

    [Fact]
    public void AFaceInsideTheViewportKeepsItsMaskWhileThePageScrolls()
    {
        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        var tracker = new SubjectTracker();
        var face = new PixelRect(500, 300, 100, 120);
        tracker.Update(new[] { Face(face) }, 1000, Monitor);

        var tracks = tracker.Update(Array.Empty<DetectionRecord>(), 1100, Monitor, Motion(0, -60));
        var mask = Assert.Single(CensorComposer.Build(tracks, settings, Monitor, 1100, false));
        Assert.True(Math.Abs(mask.Box.Y - (face.Y - 60)) <= 30,
            $"the mask did not follow the content: {mask.Box.Y:0.#} instead of {face.Y - 60:0.#}");
    }
}
