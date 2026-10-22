using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Safety;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using DetectionRecord = VeilDesk.Core.Detection.Detection;

namespace VeilDesk.Core.Tests;

public class ProtectionRegressionTests
{
    private static ProtectionSupervisor Started()
    {
        var supervisor = new ProtectionSupervisor();
        supervisor.ApplySettings(new AppSettings());
        supervisor.Start(0);
        return supervisor;
    }

    private static void Success(ProtectionSupervisor s, long time)
    {
        s.ReportFrame(time);
        s.ReportSuccess(time);
    }

    [Fact]
    public void StartupRequiresDistinctSuccessesAndMinimumDwell()
    {
        var s = Started();
        Success(s, 100);
        Success(s, 200);
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(999));
        Assert.Equal(ProtectionState.Protecting, s.Evaluate(1000));
    }

    [Fact]
    public void ReplayedFrameCannotCountAsTwoRecoveryPasses()
    {
        var s = Started();
        Success(s, 100);
        for (var i = 0; i < 100; i++) s.ReportSuccess(100);
        Assert.Equal(1, s.ConsecutiveSuccesses);
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(1000));
    }

    [Fact]
    public void PendingInferenceDoesNotLiftCoverOrDiscardLastEvidence()
    {
        var s = Started();
        Success(s, 100); Success(s, 1000);
        Assert.Equal(ProtectionState.Protecting, s.Evaluate(1000));
        s.ReportAttempt(2400);
        Assert.Equal(ProtectionState.Protecting, s.Evaluate(2400));
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(2501));
        s.ReportAttempt(2600);
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(2600));
    }

    [Fact]
    public void MissingCaptureCannotBeRenewedByAnalysisSuccess()
    {
        var s = Started();
        Success(s, 100); Success(s, 1000);
        Assert.Equal(ProtectionState.Protecting, s.Evaluate(1000));
        s.ReportSuccess(2501);
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(2501));
    }

    [Fact]
    public void StaleAndOutOfOrderResultsCannotRenewEvidence()
    {
        var s = Started();
        Success(s, 100); Success(s, 1000);
        s.ReportSuccess(500);
        Assert.Equal(1000, s.LastSuccessMs);
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(2501));
        Success(s, 2600);
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(3600));
        Success(s, 3601);
        Assert.Equal(ProtectionState.Protecting, s.Evaluate(3601));
    }

    [Fact]
    public void AlternatingFailuresCannotOscillateOutOfFailClosed()
    {
        var s = Started();
        var transitions = 0;
        s.StateChanged += (_, _, _) => transitions++;
        for (var time = 100; time <= 10000; time += 100)
        {
            if (time % 200 == 0) s.ReportFailure(time, "synthetic failure");
            else Success(s, time);
            Assert.Equal(ProtectionState.FailClosed, s.Evaluate(time));
        }
        Assert.Equal(0, transitions);
    }

    [Fact]
    public void CaptureFailureRequiresFreshRecoveryPasses()
    {
        var s = Started();
        Success(s, 100); Success(s, 1000); s.Evaluate(1000);
        s.ReportCaptureFailure("synthetic");
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(1100));
        s.ReportCaptureHealthy();
        Assert.Equal(ProtectionState.FailClosed, s.Evaluate(2100));
        Success(s, 2200); Success(s, 2300);
        Assert.Equal(ProtectionState.Protecting, s.Evaluate(2300));
    }

    [Fact]
    public void RepeatedStopAndRestartCannotResurrectStoppedProtection()
    {
        var s = Started();
        for (var run = 0; run < 100; run++)
        {
            s.Start(run * 10000);
            s.Stop();
            Success(s, run * 10000 + 100);
            s.ReportFailure(run * 10000 + 200, "late result");
            Assert.Equal(ProtectionState.Stopped, s.Evaluate(run * 10000 + 2000));
            Assert.False(s.EffectiveFailClosed(new AppSettings()));
        }
    }

    [Fact]
    public void ConfidenceMustBeAcquiredBeforeLowerReleaseThresholdApplies()
    {
        var gate = new ConfidenceGate();
        Assert.False(gate.Allows(1, .34, .35, .7));
        Assert.True(gate.Allows(1, .36, .35, .7));
        for (var i = 0; i < 100; i++)
            Assert.True(gate.Allows(1, i % 2 == 0 ? .34 : .36, .35, .7));
        Assert.False(gate.Allows(1, .20, .35, .7));
        Assert.False(gate.Allows(1, .34, .35, .7));
    }

    [Fact]
    public void TrackSnapshotsDoNotMutateDuringNextAnalysis()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1000, 800);
        var previous = tracker.Update(new[] { new DetectionRecord(BodyRegion.Person, .9,
            new PixelRect(100, 100, 200, 200)) }, 0, bounds).Single();
        tracker.Update(new[] { new DetectionRecord(BodyRegion.Person, .6,
            new PixelRect(120, 100, 200, 200)) }, 100, bounds);
        Assert.Equal(100, previous.Box.X);
        Assert.Equal(.9, previous.Confidence);
    }

    [Fact]
    public void LostTrackIsCoveredThroughPersistenceAndRecoversSameId()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1000, 800);
        var settings = new AppSettings();
        settings.Detection.EnabledRegions.Add(BodyRegion.FaceFemale);
        var detection = new DetectionRecord(BodyRegion.FaceFemale, .9, new PixelRect(100, 100, 100, 100));
        var id = tracker.Update(new[] { detection }, 0, bounds).Single().Id;
        var missed = tracker.Update(Array.Empty<DetectionRecord>(), 1000, bounds);
        Assert.Single(CensorComposer.Build(missed, settings, bounds, 1000, false));
        Assert.Equal(id, tracker.Update(new[] { detection }, 1100, bounds).Single().Id);
    }

    [Theory]
    [InlineData(-1920, 0, 1.5)]
    [InlineData(2560, -1080, 2.0)]
    public void ScrollingAndDisplayOffsetsKeepRegionsWithinLocalPhysicalBounds(int x, int y, double dpi)
    {
        var display = new MonitorDescriptor("synthetic", "synthetic", "synthetic", x, y, 1920, 1080, dpi, false);
        var tracker = new SubjectTracker();
        var settings = new AppSettings();
        settings.Detection.WholeBodyEnabled = true;
        settings.Detection.WholeBodyNudeOnly = false;
        tracker.Update(new[] { new DetectionRecord(BodyRegion.Person, .9, new PixelRect(100, 300, 200, 200)) }, 0, display.LocalBounds);
        var tracks = tracker.Update(new[] { new DetectionRecord(BodyRegion.Person, .9, new PixelRect(100, 100, 200, 200)) }, 100, display.LocalBounds);
        var regions = CensorComposer.Build(tracks, settings, display.LocalBounds, 150, false);
        Assert.NotEmpty(regions);
        foreach (var region in regions) Assert.Equal(region.Box, region.Box.Clip(display.LocalBounds));
        var guard = CensorComposer.Build(tracks, settings, display.LocalBounds, 2000, true);
        Assert.Equal(display.LocalBounds, guard.Single(r => r.Kind == CensorRegionKind.FailClosed).Box);
    }
}
