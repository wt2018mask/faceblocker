using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;

namespace VeilDesk.Core.Tests;

public class FaceRegressionTests
{
    [Fact]
    public void UncertainFaceGetsExpandedCoverAndStrictModeRemainsAvailable()
    {
        var s = new AppSettings();
        s.Detection.EnabledRegions = new() { BodyRegion.Face };
        var bounds = new PixelRect(0, 0, 1280, 720);
        var box = new PixelRect(300, 200, 80, 100);
        var tracker = new SubjectTracker();
        var tracks = tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, 0.354, box) }, 1000, bounds);
        var mask = Assert.Single(CensorComposer.Build(tracks, s, bounds, 1000, false));
        Assert.Equal(CensorRegionKind.UncertainFace, mask.Kind);
        Assert.True(mask.Box.Area > box.Area * 4);
        Assert.Equal(box.Area, mask.Box.IntersectionArea(box));
        var guard = new VeilDesk.Core.Safety.CoverageGuard();
        Assert.False(guard.RequiresCover(tracks.Select(t => t.Id), new[] { mask.TrackId!.Value }, tracks.Select(t => t.Id)));
        s.Detection.LocalFaceUncertaintyCover = false;
        Assert.Empty(CensorComposer.Build(tracks, s, bounds, 1000, false));
        Assert.True(guard.RequiresCover(tracks.Select(t => t.Id), Array.Empty<int>(), tracks.Select(t => t.Id)));
    }

    [Fact]
    public void FaceUsesIndependentConfidenceAndPersistsThroughHighRateMisses()
    {
        var s = new AppSettings();
        s.Detection.EnabledRegions = new() { BodyRegion.Face };
        s.Detection.FaceConfidence = 0.6;
        s.Detection.Confidence = 0.95;
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1280, 720);
        tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, 0.9, new(100, 100, 80, 100)) }, 1000, bounds);
        for (var i = 1; i <= 40; i++) tracker.Update(Array.Empty<VeilDesk.Core.Detection.Detection>(), 1000 + i * 10, bounds);
        var masks = CensorComposer.Build(tracker.Snapshot(), s, bounds, 1500, false);
        Assert.Single(masks);
        Assert.Equal(BodyRegion.Face, masks[0].Source);
        Assert.Empty(CensorComposer.Build(tracker.Snapshot(), s, bounds, 3001, false));
    }

    [Fact]
    public void ScrollReversalPredictionStillIncludesLatestObservedFace()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1280, 720);
        foreach (var (y, time) in new[] { (100, 1000L), (250, 1100L), (80, 1200L) })
        {
            var raw = new PixelRect(300, y, 90, 110);
            var tracks = tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, 0.9, raw) }, time, bounds);
            Assert.Contains(tracks, t => t.PredictedBox(time, 80, 0.18, bounds).IntersectionArea(raw) >= raw.Area - 0.001);
        }
    }

    [Fact]
    public void IndependentFaceSelectionAndConfidencePersist()
    {
        var s = new AppSettings();
        s.Detection.EnabledRegions = new() { BodyRegion.Face, BodyRegion.ButtocksExposed };
        s.Detection.FaceConfidence = 0.72;
        s.Detection.LocalFaceUncertaintyCover = false;
        var clone = s.Clone();
        Assert.Equal(0.72, clone.Detection.FaceConfidence);
        Assert.False(clone.Detection.LocalFaceUncertaintyCover);
        Assert.True(clone.AnyRegionEnabled);
        Assert.Contains(BodyRegion.Face, clone.Detection.EnabledRegions);
        Assert.Contains(BodyRegion.ButtocksExposed, clone.Detection.EnabledRegions);
        Assert.False(clone.Detection.WholeBodyEnabled);
    }
}
