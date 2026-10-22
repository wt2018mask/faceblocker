using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;

namespace VeilDesk.Core.Censoring;

public enum CensorRegionKind
{
    Detection,
    WholePerson,
    FailClosed
}

public sealed record CensorRegion(
    int? TrackId,
    BodyRegion Source,
    CensorRegionKind Kind,
    PixelRect Box,
    double Confidence,
    byte[]? Mask,
    int MaskWidth,
    int MaskHeight)
{
    public bool HasMask => Mask is { Length: > 0 } && MaskWidth > 0 && MaskHeight > 0;
}

/// <summary>
/// Turns tracked detections into renderable censored regions. This is where
/// motion prediction, persistence, confidence filtering and conservative
/// fail-closed behavior are unified, independently of WPF or the capture API.
/// </summary>
public static class CensorComposer
{
    public static IReadOnlyList<CensorRegion> Build(
        IEnumerable<TrackedDetection> tracks,
        AppSettings settings,
        PixelRect monitorBounds,
        long nowMs,
        bool failClosedActive)
    {
        var result = new List<CensorRegion>(16);
        var censoring = settings.Censoring;
        var detection = settings.Detection;
        var persistence = censoring.TrackPersistenceMs;

        // Confidence hysteresis. A mask is acquired at the configured
        // threshold but only released below a lower one, so a detector whose
        // score oscillates around the threshold keeps the mask instead of
        // flickering it on and off. The track confidence itself is already
        // smoothed by the tracker.
        var releaseRatio = Math.Clamp(censoring.ConfidenceReleaseRatio, 0.1, 1.0);
        var bodyRelease = detection.Confidence * releaseRatio;
        var personRelease = detection.WholeBodyConfidence * releaseRatio;

        foreach (var track in tracks)
        {
            var age = nowMs - track.LastSeenMs;
            if (age > persistence) continue;

            if (track.IsPerson)
            {
                if (!detection.WholeBodyEnabled) continue;
                if (track.Confidence < personRelease) continue;
                if (detection.WholeBodyNudeOnly && !track.HasExposedEvidence) continue;
                if (track.Gender == PersonGender.Female && !detection.WholeBodyFemale) continue;
                if (track.Gender == PersonGender.Male && !detection.WholeBodyMale) continue;
                // Unknown gender is treated conservatively: include if either switch is on.
                if (track.Gender == PersonGender.Unknown && !detection.WholeBodyFemale && !detection.WholeBodyMale) continue;

                var personBox = track.PredictedBox(nowMs, censoring.PredictionHorizonMs, censoring.MotionPadding, monitorBounds)
                    .Expand(censoring.OutlineMargin);
                if (personBox.IsEmpty) continue;

                result.Add(new CensorRegion(
                    track.Id,
                    BodyRegion.Person,
                    CensorRegionKind.WholePerson,
                    personBox,
                    track.Confidence,
                    track.Mask,
                    track.MaskWidth,
                    track.MaskHeight));
                continue;
            }

            if (!detection.EnabledRegions.Contains(track.Region)) continue;
            if (track.Confidence < bodyRelease) continue;

            var box = track.PredictedBox(nowMs, censoring.PredictionHorizonMs, censoring.MotionPadding, monitorBounds)
                .Expand(censoring.MaskExpansion);
            if (box.IsEmpty || box.Area < 16) continue;

            result.Add(new CensorRegion(
                track.Id,
                track.Region,
                CensorRegionKind.Detection,
                box,
                track.Confidence,
                track.Mask,
                track.MaskWidth,
                track.MaskHeight));
        }

        if (failClosedActive)
        {
            result.Add(new CensorRegion(
                null,
                BodyRegion.Person,
                CensorRegionKind.FailClosed,
                monitorBounds,
                0,
                null,
                0,
                0));
        }

        return result;
    }

    /// <summary>Merges the tracked bounding boxes of all regions into quick debug data.</summary>
    public static PixelRect BoundingUnion(IEnumerable<CensorRegion> regions)
    {
        var union = PixelRect.Empty;
        foreach (var region in regions) union = union.Union(region.Box);
        return union;
    }
}
