using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;

namespace VeilDesk.Core.Tests;

public class FaceLocalityRegressionTests
{
    [Theory]
    [InlineData(.9)]
    [InlineData(.354)]
    public void HeldMovingFaceCannotExpandAcrossTheDisplay(double confidence)
    {
        var s = new AppSettings();
        s.Detection.EnabledRegions = new() { BodyRegion.Face };
        var bounds = new PixelRect(0, 0, 2560, 1600);
        var tracker = new SubjectTracker();
        tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, confidence, new(600, 400, 189, 181)) }, 1000, bounds);
        var raw = new PixelRect(900, 562, 189, 181);
        var tracks = tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, confidence, raw) }, 1100, bounds);
        var mask = Assert.Single(CensorComposer.Build(tracks, s, bounds, 2900, false));
        Assert.Equal(raw.Area, mask.Box.IntersectionArea(raw), 5);
        Assert.True(mask.Box.Width <= raw.Width * 1.75 + 12, $"Mask width {mask.Box.Width} is excessive for face width {raw.Width}");
        Assert.True(mask.Box.Height <= raw.Height * 1.75 + 12, $"Mask height {mask.Box.Height} is excessive for face height {raw.Height}");
        Assert.Empty(CensorComposer.Build(tracks, s, bounds, 3101, false));
    }

    [Fact]
    public void LargeUncertainFaceGetsOneLocalMarginInsteadOfCompoundedExpansion()
    {
        var s = new AppSettings();
        s.Detection.EnabledRegions = new() { BodyRegion.Face };
        var bounds = new PixelRect(0, 0, 2560, 1600);
        var tracker = new SubjectTracker();
        var raw = new PixelRect(800, 500, 516, 512);
        var tracks = tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, .354, raw) }, 1000, bounds);
        var mask = Assert.Single(CensorComposer.Build(tracks, s, bounds, 1300, false));
        Assert.Equal(CensorRegionKind.UncertainFace, mask.Kind);
        Assert.Equal(raw.Area, mask.Box.IntersectionArea(raw), 5);
        Assert.True(mask.Box.Width <= raw.Width * 1.5 + 12);
        Assert.True(mask.Box.Height <= raw.Height * 1.5 + 12);
    }

    [Fact]
    public void MissingPassesHoldALocalMaskWithoutContinuousSpatialGrowth()
    {
        var s = new AppSettings();
        s.Detection.EnabledRegions = new() { BodyRegion.Face };
        var bounds = new PixelRect(0, 0, 2560, 1600);
        var tracker = new SubjectTracker();
        tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, .9, new(600, 400, 100, 120)) }, 1000, bounds);
        var tracks = tracker.Update(new[] { new VeilDesk.Core.Detection.Detection(BodyRegion.Face, .9, new(700, 460, 100, 120)) }, 1100, bounds);
        var first = Assert.Single(CensorComposer.Build(tracks, s, bounds, 1200, false));
        var held = Assert.Single(CensorComposer.Build(tracks, s, bounds, 2900, false));
        Assert.Equal(first.Box, held.Box);
    }
}
