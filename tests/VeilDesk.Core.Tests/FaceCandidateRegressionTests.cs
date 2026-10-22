using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using DetectionRecord = VeilDesk.Core.Detection.Detection;

namespace VeilDesk.Core.Tests;

public class FaceCandidateRegressionTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1600);

    /// <summary>Landmarks of a frontal face inside a 100x120 box at (0,0).</summary>
    private static FacePoint[] FaceLandmarks(double x, double y, double w, double h) => new[]
    {
        new FacePoint(x + w * 0.32, y + h * 0.35),
        new FacePoint(x + w * 0.68, y + h * 0.35),
        new FacePoint(x + w * 0.50, y + h * 0.55),
        new FacePoint(x + w * 0.36, y + h * 0.74),
        new FacePoint(x + w * 0.64, y + h * 0.74),
    };

    private static DetectionRecord Face(PixelRect box, double confidence, bool consistent = true)
    {
        var evidence = FaceCandidateValidator.Evaluate(box,
            consistent ? FaceLandmarks(box.X, box.Y, box.Width, box.Height)
                : new[]
                {
                    new FacePoint(box.X + box.Width * 0.95, box.Y + box.Height * 0.05),
                    new FacePoint(box.X + box.Width * 0.10, box.Y + box.Height * 0.90),
                    new FacePoint(box.X + box.Width * 0.99, box.Y + box.Height * 0.95),
                    new FacePoint(box.X - box.Width * 0.40, box.Y - box.Height * 0.30),
                    new FacePoint(box.X + box.Width * 0.05, box.Y + box.Height * 0.02),
                });
        return new DetectionRecord(BodyRegion.Face, confidence, box) { Face = evidence };
    }

    [Fact]
    public void ConsistentLandmarksValidateAFace()
    {
        var box = new PixelRect(400, 300, 100, 120);
        var evidence = FaceCandidateValidator.Evaluate(box, FaceLandmarks(box.X, box.Y, box.Width, box.Height));
        Assert.Equal(FaceCandidateQuality.Valid, evidence.Quality);
        Assert.Equal(5, evidence.Landmarks.Count);
    }

    [Fact]
    public void ContradictoryLandmarksAreNotAFace()
    {
        var box = new PixelRect(400, 300, 100, 120);
        var evidence = FaceCandidateValidator.Evaluate(box, new[]
        {
            new FacePoint(box.X + box.Width * 0.95, box.Y + box.Height * 0.05),
            new FacePoint(box.X + box.Width * 0.10, box.Y + box.Height * 0.90),
            new FacePoint(box.X + box.Width * 0.99, box.Y + box.Height * 0.95),
            new FacePoint(box.X - box.Width * 0.40, box.Y - box.Height * 0.30),
            new FacePoint(box.X + box.Width * 0.05, box.Y + box.Height * 0.02),
        });
        Assert.Equal(FaceCandidateQuality.Invalid, evidence.Quality);
        Assert.Contains("landmark-rejected", evidence.Reason);
    }

    [Fact]
    public void AGenuineLargeFaceIsAcceptedAndMaskedAtItsOwnSize()
    {
        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        var large = new PixelRect(700, 400, 900, 900);
        var candidate = Face(large, 0.86);

        var (accepted, verdicts) = FaceCandidateGate.Evaluate(new[] { candidate }, 0.6, Array.Empty<TrackedDetection>());
        Assert.Single(accepted);
        Assert.Equal(FaceCandidateDecision.Accepted, Assert.Single(verdicts).Decision);

        var tracker = new SubjectTracker();
        var tracks = tracker.Update(accepted, 1000, Monitor);
        var mask = Assert.Single(CensorComposer.Build(tracks, settings, Monitor, 1000, false));
        Assert.True(mask.Box.Width <= large.Width * 1.35,
            $"mask width {mask.Box.Width} is far larger than the detected face");
        Assert.True(mask.Box.Height <= large.Height * 1.35,
            $"mask height {mask.Box.Height} is far larger than the detected face");
    }

    [Fact]
    public void RecordedOversizedCandidateCannotHijackAnEstablishedFace()
    {
        // Recorded Instagram session, frames 1475 and 1493: a real face of
        // 186x238 pixels was replaced by a 795x977 candidate with a score of
        // 0.361, and the censor mask grew to 1199x1242 pixels, 36.4% of the
        // 2560x1600 display.
        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        var tracker = new SubjectTracker();

        var real = new PixelRect(926, 1097, 186, 238);
        var tracks = tracker.Update(new[] { Face(real, 0.909) }, 1000, Monitor);

        var bogus = new PixelRect(560, 623, 795, 977);
        var bogusCandidate = Face(bogus, 0.361, consistent: false);
        var (accepted, verdicts) = FaceCandidateGate.Evaluate(new[] { bogusCandidate }, 0.6, tracks);
        Assert.Empty(accepted);
        Assert.Equal(FaceCandidateDecision.RejectedNotAFace, Assert.Single(verdicts).Decision);

        // Even if the candidate had passed the gate, it may not take over the
        // established track because its size and score disagree with it.
        var after = tracker.Update(new[] { Face(bogus, 0.361) }, 1100, Monitor);
        var established = after.Single(t => t.Box.Area <= real.Area * 4);
        Assert.Equal(real.Area, established.Box.Area, 1);
        Assert.True(established.PeakConfidence >= 0.909);

        var mask = Assert.Single(CensorComposer.Build(after.Where(t => t.Id == established.Id).ToList(),
            settings, Monitor, 1100, false));
        Assert.True(mask.Box.Area <= real.Area * 3,
            $"mask area {mask.Box.Area} is far beyond the detected face area {real.Area}");
        Assert.True(mask.Box.Area / (Monitor.Width * Monitor.Height) < 0.05,
            "the mask still covers an unreasonable part of the display");
    }

    [Fact]
    public void CoarseDuplicateOfATrackedFaceIsRefused()
    {
        var tracker = new SubjectTracker();
        var real = new PixelRect(962, 631, 142, 178);
        var tracks = tracker.Update(new[] { Face(real, 0.909) }, 1000, Monitor);

        // A low scoring rectangle that swallows the tracked face describes the
        // same subject twice at a coarser scale.
        var coarse = new PixelRect(560, 623, 795, 977);
        var candidate = Face(coarse, 0.361);
        var (accepted, verdicts) = FaceCandidateGate.Evaluate(new[] { candidate }, 0.6, tracks);
        Assert.Empty(accepted);
        var verdict = Assert.Single(verdicts);
        Assert.Equal(FaceCandidateDecision.RejectedCoarseDuplicate, verdict.Decision);
        Assert.NotNull(verdict.DuplicateOfTrackId);
    }

    [Fact]
    public void AConfidentCoarseDuplicateIsNeverRefused()
    {
        var tracker = new SubjectTracker();
        var real = new PixelRect(962, 631, 142, 178);
        var tracks = tracker.Update(new[] { Face(real, 0.909) }, 1000, Monitor);

        var coarse = new PixelRect(560, 623, 795, 977);
        var candidate = Face(coarse, 0.91);
        var (accepted, _) = FaceCandidateGate.Evaluate(new[] { candidate }, 0.6, tracks);
        Assert.Single(accepted);
    }

    [Fact]
    public void ContradictoryCandidateGetsTheSmallestCoverThatStillHidesIt()
    {
        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        var tracker = new SubjectTracker();
        var box = new PixelRect(800, 500, 516, 512);
        var candidate = Face(box, 0.354, consistent: false);
        var tracks = tracker.Update(new[] { candidate }, 1000, Monitor);
        var mask = Assert.Single(CensorComposer.Build(tracks, settings, Monitor, 1000, false));
        Assert.Equal(CensorRegionKind.UncertainFace, mask.Kind);
        Assert.Equal(box.Area, mask.Box.IntersectionArea(box), 5);
        Assert.True(mask.Box.Width <= box.Width * 1.35,
            $"mask width {mask.Box.Width} still compounds the uncertain margin");
    }
}
