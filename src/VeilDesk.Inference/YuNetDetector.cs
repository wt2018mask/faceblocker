using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Vision;

namespace VeilDesk.Inference;

/// <summary>YuNet 2023mar: BGR 0..255 NCHW, fixed 640 square input.
/// Decoding follows OpenCV FaceDetectorYN (no landmark/identity storage).</summary>
public static class YuNetDetector
{
    public const int Size = 640;

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
    {
        var boxes = new List<Detection>();
        var transform = LetterboxTransform.CreateCentered(width, height, Size, Size);
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cells = Size / stride;
            var cls = output[$"cls_{stride}"];
            var obj = output[$"obj_{stride}"];
            var bb = output[$"bbox_{stride}"];
            if (cls.Length != cells * cells || obj.Length != cls.Length || bb.Length != cls.Length * 4)
                throw new InvalidDataException("Unexpected YuNet output dimensions.");
            for (var i = 0; i < cls.Length; i++)
            {
                var score = Math.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (!double.IsFinite(score) || score < threshold) continue;
                var cx = (i % cells + bb[4 * i]) * stride;
                var cy = (i / cells + bb[4 * i + 1]) * stride;
                var w = Math.Exp(bb[4 * i + 2]) * stride;
                var h = Math.Exp(bb[4 * i + 3]) * stride;
                if (!double.IsFinite(cx + cy + w + h)) continue;
                var box = transform.MapCenterBoxToSource(cx, cy, w, h);
                if (!box.IsEmpty) boxes.Add(new Detection(BodyRegion.Face, score, box));
            }
        }
        return NonMaximumSuppression.Suppress(boxes.OrderByDescending(b => b.Confidence).Take(5000).ToList(), nms);
    }
}
