using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Vision;

namespace VeilDesk.Inference;

/// <summary>YuNet 2023mar: BGR 0..255 NCHW, fixed 640 square input.
/// Decoding follows OpenCV FaceDetectorYN (no landmark/identity storage).</summary>
public static class YuNetDetector
{
    public const int Size = 640;

    /// <summary>
    /// The strides the exported graph produces, and the number of grid cells
    /// for each of them at the fixed 640 square input.
    /// </summary>
    private static readonly int[] Strides = { 8, 16, 32 };

    public static void Preprocess(VideoFrame frame, float[] input)
    {
        FramePreprocessor.PreprocessYolo(frame, Size, input);
        // The existing sampler emits normalized RGB. YuNet uses raw BGR and black padding.
        var scale = Math.Min((double)Size / frame.Width, (double)Size / frame.Height);
        var px = (Size - frame.Width * scale) / 2;
        var py = (Size - frame.Height * scale) / 2;
        var plane = Size * Size;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var i = y * Size + x;
            if (x < px || y < py || (x - px) / scale > frame.Width - 1 || (y - py) / scale > frame.Height - 1)
            { input[i] = input[plane + i] = input[2 * plane + i] = 0; continue; }
            var r = input[i];
            input[i] = input[2 * plane + i] * 255;
            input[plane + i] *= 255;
            input[2 * plane + i] = r * 255;
        }
    }

    public static IReadOnlyList<Detection> Decode(IReadOnlyDictionary<string, float[]> output,
        int width, int height, double threshold, double nms)
        => DecodeCandidates(output, width, height, threshold, nms)
            .Select(c => c.Detection).ToList();

    /// <summary>
    /// Decodes the raw graph output into face candidates with landmarks.
    ///
    /// The box decode is identical to the reference implementation:
    /// score = sqrt(cls * obj), centre = (cell + offset) * stride and
    /// size = exp(logSize) * stride, all in model space. The landmarks use the
    /// reference formula as well: point = (kps + cell) * stride, which is
    /// relative to the anchor cell and not to the decoded box.
    /// </summary>
    public static IReadOnlyList<FaceCandidate> DecodeCandidates(IReadOnlyDictionary<string, float[]> output,
        int width, int height, double threshold, double nms)
    {
        var candidates = new List<FaceCandidate>();
        var transform = LetterboxTransform.CreateCentered(width, height, Size, Size);
        foreach (var stride in Strides)
        {
            var cells = Size / stride;
            var cls = output[$"cls_{stride}"];
            var obj = output[$"obj_{stride}"];
            var bb = output[$"bbox_{stride}"];
            var kps = output.TryGetValue($"kps_{stride}", out var kpsOut) ? kpsOut : null;
            if (cls.Length != cells * cells || obj.Length != cls.Length || bb.Length != cls.Length * 4)
                throw new InvalidDataException("Unexpected YuNet output dimensions.");
            if (kps is not null && kps.Length != cls.Length * 10)
                throw new InvalidDataException("Unexpected YuNet landmark output dimensions.");

            for (var i = 0; i < cls.Length; i++)
            {
                var score = Math.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (!double.IsFinite(score) || score < threshold) continue;
                var col = i % cells;
                var row = i / cells;
                var cx = (col + bb[4 * i]) * stride;
                var cy = (row + bb[4 * i + 1]) * stride;
                var w = Math.Exp(bb[4 * i + 2]) * stride;
                var h = Math.Exp(bb[4 * i + 3]) * stride;
                if (!double.IsFinite(cx + cy + w + h)) continue;
                var box = transform.MapCenterBoxToSource(cx, cy, w, h);
                if (box.IsEmpty) continue;

                var landmarks = Array.Empty<FacePoint>();
                if (kps is not null)
                {
                    var points = new FacePoint[FaceCandidateValidator.LandmarkCount];
                    for (var n = 0; n < points.Length; n++)
                    {
                        // Reference formula: the landmark is placed relative to
                        // the anchor cell, not to the decoded box.
                        var px = (kps[10 * i + 2 * n] + col) * stride;
                        var py = (kps[10 * i + 2 * n + 1] + row) * stride;
                        var sx = (px - transform.PadX) / transform.Scale;
                        var sy = (py - transform.PadY) / transform.Scale;
                        points[n] = new FacePoint(sx, sy);
                    }
                    landmarks = points;
                }

                var evidence = FaceCandidateValidator.Evaluate(box, landmarks);
                candidates.Add(new FaceCandidate(
                    new Detection(BodyRegion.Face, score, box) { Face = evidence },
                    evidence, score, box, landmarks, stride));
            }
        }

        return Suppress(candidates, nms);
    }

    /// <summary>
    /// Plain IoU suppression, exactly as the reference implementation does it.
    /// The extra "coarse duplicate" rules live in the candidate gate, where
    /// the configured face confidence is known.
    /// </summary>
    private static IReadOnlyList<FaceCandidate> Suppress(IReadOnlyList<FaceCandidate> candidates, double nms)
    {
        if (candidates.Count <= 1) return candidates;
        var ordered = candidates.OrderByDescending(c => c.Confidence).ToList();
        var kept = new List<FaceCandidate>(candidates.Count);
        foreach (var candidate in ordered)
        {
            var duplicate = kept.Any(other => other.Box.IoU(candidate.Box) >= nms);
            if (!duplicate) kept.Add(candidate);
        }
        return kept;
    }
}

/// <summary>One decoded raw face candidate and the evidence attached to it.</summary>
public sealed record FaceCandidate(
    Detection Detection,
    FaceCandidateEvidence Evidence,
    double Confidence,
    PixelRect Box,
    IReadOnlyList<FacePoint> Landmarks,
    int Stride);
