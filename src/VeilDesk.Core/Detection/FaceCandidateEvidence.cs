using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Detection;

/// <summary>How well a face candidate is supported by its own geometry.</summary>
public enum FaceCandidateQuality
{
    /// <summary>Landmarks and box geometry agree with a real face.</summary>
    Valid,

    /// <summary>Some checks failed. The candidate is still masked, but with the
    /// smallest cover that contains the observed face.</summary>
    Suspicious,

    /// <summary>The candidate contradicts the geometry of a face. It must not
    /// create a track and must not take over an existing one.</summary>
    Invalid,
}

/// <summary>One decoded facial landmark in monitor-local physical pixels.</summary>
public readonly record struct FacePoint(double X, double Y);

/// <summary>
/// Metadata about one raw face candidate. It carries the decoded landmarks and
/// the consistency verdict so that later stages can explain, in the audit log,
/// why a candidate was masked, refined or rejected.
/// </summary>
public sealed record FaceCandidateEvidence(
    IReadOnlyList<FacePoint> Landmarks,
    FaceCandidateQuality Quality,
    string Reason,
    double Agreement)
{
    /// <summary>Landmarks are only meaningful when all five were decoded.</summary>
    public bool HasLandmarks => Landmarks.Count == FaceCandidateValidator.LandmarkCount;

    /// <summary>Evidence for a candidate that was produced without landmarks.</summary>
    public static FaceCandidateEvidence WithoutLandmarks(string reason) =>
        new(Array.Empty<FacePoint>(), FaceCandidateQuality.Suspicious, reason, 0);
}
