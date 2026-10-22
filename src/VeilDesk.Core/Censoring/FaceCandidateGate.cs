using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Tracking;
using DetectionRecord = VeilDesk.Core.Detection.Detection;

namespace VeilDesk.Core.Censoring;

/// <summary>Why a raw face candidate was admitted or refused.</summary>
public enum FaceCandidateDecision
{
    /// <summary>The candidate may create or update a track.</summary>
    Accepted,

    /// <summary>The candidate contradicts the geometry of a face.</summary>
    RejectedNotAFace,

    /// <summary>The candidate is a coarse duplicate of a better scored face.</summary>
    RejectedCoarseDuplicate,
}

/// <summary>One admitted or refused raw face candidate with its reason.</summary>
public sealed record FaceCandidateVerdict(
    int TrackId,
    FaceCandidateDecision Decision,
    string Reason,
    PixelRect Box,
    double Confidence,
    int? DuplicateOfTrackId = null);

/// <summary>
/// Decides which raw face candidates are allowed to become censor tracks.
///
/// Two failure modes are handled here, and both were observed in the recorded
/// Instagram session:
///
/// 1. A background anchor reached the admission threshold and produced a
///    screen-sized rectangle (795x977 pixels on a 2560x1600 display). The
///    rectangle was then expanded by the usual face margins into a mask that
///    covered 36% of the screen.
/// 2. That rectangle swallowed a real, much better scored face and was
///    associated with the real face's track, which replaced a correct small
///    mask with the oversized one.
///
/// The gate therefore refuses a candidate only when the evidence is specific:
/// the candidate is below the configured face confidence, and either its own
/// landmarks contradict a face or it geometrically contains a face that is
/// already tracked with a better score. A confident detection is never
/// refused, so a genuinely large face is still censored at full size.
/// </summary>
public static class FaceCandidateGate
{
    /// <summary>How much of the tracked face must lie inside the candidate.</summary>
    private const double ContainmentRatio = 0.85;

    /// <summary>The candidate must be this much larger than the face it swallows.</summary>
    private const double ContainmentAreaRatio = 2.5;

    public static (IReadOnlyList<DetectionRecord> Accepted, IReadOnlyList<FaceCandidateVerdict> Verdicts)
        Evaluate(IReadOnlyList<DetectionRecord> candidates, double faceConfidence,
            IReadOnlyList<TrackedDetection> existingTracks)
    {
        var accepted = new List<DetectionRecord>(candidates.Count);
        var verdicts = new List<FaceCandidateVerdict>(candidates.Count);
        if (candidates.Count == 0)
            return (accepted, verdicts);

        var trusted = candidates.Where(c => c.Confidence >= faceConfidence).ToList();

        foreach (var candidate in candidates)
        {
            var isTrusted = candidate.Confidence >= faceConfidence;

            // A confident detection is never refused on geometry alone.
            var quality = candidate.Face?.Quality ?? FaceCandidateQuality.Suspicious;
            if (!isTrusted && quality == FaceCandidateQuality.Invalid)
            {
                verdicts.Add(new FaceCandidateVerdict(0, FaceCandidateDecision.RejectedNotAFace,
                    candidate.Face?.Reason ?? "no-evidence", candidate.Box, candidate.Confidence));
                continue;
            }

            // Coarse duplicate of a face that is already tracked.
            var duplicate = FindDuplicate(candidate, existingTracks);
            if (!isTrusted && duplicate is not null)
            {
                verdicts.Add(new FaceCandidateVerdict(duplicate.Id,
                    FaceCandidateDecision.RejectedCoarseDuplicate,
                    $"contains-track-{duplicate.Id}", candidate.Box, candidate.Confidence,
                    duplicate.Id));
                continue;
            }

            // Coarse duplicate of a better scored candidate in the same frame.
            var sibling = FindDuplicate(candidate, trusted.Where(t => !ReferenceEquals(t, candidate)));
            if (!isTrusted && sibling is not null)
            {
                verdicts.Add(new FaceCandidateVerdict(0, FaceCandidateDecision.RejectedCoarseDuplicate,
                    "contains-better-candidate", candidate.Box, candidate.Confidence));
                continue;
            }

            accepted.Add(candidate);
            verdicts.Add(new FaceCandidateVerdict(0, FaceCandidateDecision.Accepted,
                candidate.Face?.Reason ?? "accepted", candidate.Box, candidate.Confidence));
        }

        return (accepted, verdicts);
    }

    /// <summary>
    /// Finds a tracked face that this candidate swallows. The candidate must
    /// be clearly larger, must contain almost all of the tracked face, and must
    /// not score better than it.
    /// </summary>
    private static TrackedDetection? FindDuplicate(DetectionRecord candidate,
        IReadOnlyList<TrackedDetection> tracks)
    {
        foreach (var track in tracks)
        {
            if (track.Region != BodyRegion.Face) continue;
            if (candidate.Box.Area <= track.Box.Area * ContainmentAreaRatio) continue;
            if (candidate.Box.OverlapOfSmaller(track.Box) < ContainmentRatio) continue;
            if (candidate.Confidence > track.Confidence) continue;
            return track;
        }
        return null;
    }

    private static DetectionRecord? FindDuplicate(DetectionRecord candidate,
        IEnumerable<DetectionRecord> others)
    {
        foreach (var other in others)
        {
            if (ReferenceEquals(other, candidate)) continue;
            if (candidate.Box.Area <= other.Box.Area * ContainmentAreaRatio) continue;
            if (candidate.Box.OverlapOfSmaller(other.Box) < ContainmentRatio) continue;
            if (candidate.Confidence > other.Confidence) continue;
            return other;
        }
        return null;
    }
}
