using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Detection;

/// <summary>
/// Judges whether a raw face candidate is geometrically consistent with a face.
///
/// The detector produces five landmarks (right eye, left eye, nose tip, right
/// mouth corner, left mouth corner). A genuine face keeps them in a stable
/// arrangement: both eyes in the upper half, the nose below them, the mouth
/// below that, the eye and mouth pairs symmetric about the box centre, and the
/// nose horizontally between the eyes. A background anchor that happens to
/// reach the score threshold does not.
///
/// The thresholds are deliberately generous. They are meant to separate a face
/// from "clearly not a face", never to judge pose, beauty or identity. A large
/// genuine face is exactly as valid as a small one.
/// </summary>
public static class FaceCandidateValidator
{
    public const int LandmarkCount = 5;

    // Order used by the official YuNet output: right eye, left eye, nose tip,
    // right mouth corner, left mouth corner.
    private const int RightEye = 0;
    private const int LeftEye = 1;
    private const int Nose = 2;
    private const int RightMouth = 3;
    private const int LeftMouth = 4;

    /// <summary>Fraction of landmarks that must lie inside the candidate box.</summary>
    private const double MinimumInsideRatio = 0.8;

    /// <summary>Distance between the eyes relative to the box width.</summary>
    private const double MinimumEyeSpan = 0.12;
    private const double MaximumEyeSpan = 0.80;

    /// <summary>Horizontal symmetry of the eye and mouth pairs.</summary>
    private const double MaximumEyeSymmetry = 0.30;
    private const double MaximumMouthSymmetry = 0.35;

    /// <summary>Horizontal distance between the nose and the eye midpoint.</summary>
    private const double MaximumNoseOffset = 0.28;

    /// <summary>Vertical placement of the landmark groups inside the box.</summary>
    private const double MinimumEyeHeight = 0.10;
    private const double MaximumEyeHeight = 0.65;
    private const double MinimumNoseHeight = 0.25;
    private const double MaximumNoseHeight = 0.85;
    private const double MinimumMouthHeight = 0.42;

    /// <summary>Allowed width/height ratio of a face box, including profiles.</summary>
    private const double MinimumAspect = 0.30;
    private const double MaximumAspect = 2.20;

    /// <summary>
    /// Evaluates a candidate. Never throws: a candidate without usable
    /// landmarks is reported as <see cref="FaceCandidateQuality.Suspicious"/>
    /// rather than being treated as a proven face.
    /// </summary>
    public static FaceCandidateEvidence Evaluate(PixelRect box, IReadOnlyList<FacePoint> landmarks)
    {
        if (box.IsEmpty || box.Width < 2 || box.Height < 2)
            return new FaceCandidateEvidence(landmarks, FaceCandidateQuality.Invalid, "empty-box", 0);
        if (landmarks is null || landmarks.Count != LandmarkCount)
            return FaceCandidateEvidence.WithoutLandmarks("landmark-count");

        var w = box.Width;
        var h = box.Height;

        var inside = 0;
        foreach (var p in landmarks)
        {
            if (p.X >= box.X - 0.05 * w && p.X <= box.Right + 0.05 * w &&
                p.Y >= box.Y - 0.05 * h && p.Y <= box.Bottom + 0.05 * h)
                inside++;
        }
        var insideRatio = inside / (double)LandmarkCount;

        var rightEye = landmarks[RightEye];
        var leftEye = landmarks[LeftEye];
        var nose = landmarks[Nose];
        var rightMouth = landmarks[RightMouth];
        var leftMouth = landmarks[LeftMouth];

        var eyeMidX = (rightEye.X + leftEye.X) / 2.0;
        var eyeMidY = (rightEye.Y + leftEye.Y) / 2.0;
        var mouthMidX = (rightMouth.X + leftMouth.X) / 2.0;
        var mouthMidY = (rightMouth.Y + leftMouth.Y) / 2.0;

        var eyeSpan = Math.Abs(leftEye.X - rightEye.X) / w;
        var mouthSpan = Math.Abs(leftMouth.X - rightMouth.X) / w;
        var eyeHeight = (eyeMidY - box.Y) / h;
        var noseHeight = (nose.Y - box.Y) / h;
        var mouthHeight = (mouthMidY - box.Y) / h;
        var eyeSymmetry = Math.Abs(eyeMidX - box.CenterX) / w;
        var mouthSymmetry = Math.Abs(mouthMidX - box.CenterX) / w;
        var noseOffset = Math.Abs(nose.X - eyeMidX) / w;
        var aspect = w / h;

        // Vertical order with a tolerance of 6% of the box height so that a
        // tilted or partially occluded face is not rejected.
        var tolerance = 0.06 * h;
        var ordered = eyeMidY <= nose.Y + tolerance && nose.Y <= mouthMidY + tolerance;

        var agreement = 0.0;
        var checks = 0;

        bool Check(bool ok)
        {
            checks++;
            if (ok) agreement += 1.0 / 8.0;
            return ok;
        }

        var criticalOk =
            Check(insideRatio >= MinimumInsideRatio) &&
            Check(ordered) &&
            Check(eyeSpan >= MinimumEyeSpan && eyeSpan <= MaximumEyeSpan) &&
            Check(eyeSymmetry <= MaximumEyeSymmetry) &&
            Check(noseOffset <= MaximumNoseOffset);

        var secondaryOk =
            Check(eyeHeight >= MinimumEyeHeight && eyeHeight <= MaximumEyeHeight) &&
            Check(noseHeight >= MinimumNoseHeight && noseHeight <= MaximumNoseHeight) &&
            Check(mouthHeight >= MinimumMouthHeight) &&
            Check(mouthSymmetry <= MaximumMouthSymmetry) &&
            Check(mouthSpan >= 0.08 && mouthSpan <= 0.75) &&
            Check(aspect >= MinimumAspect && aspect <= MaximumAspect);

        var quality = criticalOk
            ? (secondaryOk ? FaceCandidateQuality.Valid : FaceCandidateQuality.Suspicious)
            : FaceCandidateQuality.Invalid;

        var reason = criticalOk
            ? (secondaryOk
                ? "landmark-consistent"
                : $"landmark-secondary:{Describe(insideRatio, ordered, eyeSpan, eyeSymmetry, noseOffset)}")
            : $"landmark-rejected:{Describe(insideRatio, ordered, eyeSpan, eyeSymmetry, noseOffset)}";

        return new FaceCandidateEvidence(landmarks, quality, reason, agreement);
    }

    private static string Describe(double insideRatio, bool ordered, double eyeSpan,
        double eyeSymmetry, double noseOffset)
    {
        var parts = new List<string>(5);
        if (insideRatio < MinimumInsideRatio) parts.Add($"inside={insideRatio:0.00}");
        if (!ordered) parts.Add("order");
        if (eyeSpan < MinimumEyeSpan || eyeSpan > MaximumEyeSpan) parts.Add($"eyeSpan={eyeSpan:0.00}");
        if (eyeSymmetry > MaximumEyeSymmetry) parts.Add($"eyeSym={eyeSymmetry:0.00}");
        if (noseOffset > MaximumNoseOffset) parts.Add($"nose={noseOffset:0.00}");
        return parts.Count == 0 ? "ok" : string.Join('|', parts);
    }

    /// <summary>
    /// The face core implied by the landmarks. It is the rectangle that the
    /// five points span, grown to a plausible face shape and clipped to the
    /// candidate box. It is used to keep an uncertain candidate from covering
    /// unrelated content.
    /// </summary>
    public static PixelRect CoreBox(PixelRect box, IReadOnlyList<FacePoint> landmarks)
    {
        if (landmarks is null || landmarks.Count != LandmarkCount || box.IsEmpty) return box;

        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;
        foreach (var p in landmarks)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }
        if (maxX <= minX || maxY <= minY) return box;

        var width = maxX - minX;
        var height = maxY - minY;
        // The five points span roughly the middle band of a face. Extend the
        // landmark rectangle to the full face box, but never outside it.
        var coreWidth = Math.Min(box.Width, width * 2.1);
        var coreHeight = Math.Min(box.Height, height * 2.4);
        var cx = (minX + maxX) / 2.0;
        var cy = (minY + maxY) / 2.0;
        var core = new PixelRect(cx - coreWidth / 2.0, cy - coreHeight / 2.0, coreWidth, coreHeight);
        return core.Clip(box);
    }
}
