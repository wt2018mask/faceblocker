using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Detection;

public static class DetectionDecoder
{
    /// <summary>
    /// Decodes NudeNet 320n output. The exported graph returns
    /// [1, 4 + 18, anchors] where every anchor has center-x, center-y, width,
    /// height followed by 18 class scores. This is the layout used by NudeNet
    /// 3.4.x and verified against the shipped model.
    /// </summary>
    public static IReadOnlyList<Detection> DecodeNudeNet(
        ReadOnlySpan<float> output,
        int channels,
        int anchors,
        LetterboxTransform transform,
        double confidenceThreshold,
        double nmsIou,
        Func<BodyRegion, bool>? classFilter = null)
    {
        if (channels < 5 || anchors <= 0 || output.Length < channels * anchors)
            return Array.Empty<Detection>();

        var classCount = Math.Min(18, channels - 4);
        var candidates = new List<Detection>(32);

        for (var a = 0; a < anchors; a++)
        {
            var bestScore = 0f;
            var bestClass = -1;
            for (var c = 0; c < classCount; c++)
            {
                var score = output[(4 + c) * anchors + a];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || bestScore < confidenceThreshold) continue;
            var region = (BodyRegion)bestClass;
            if (classFilter is not null && !classFilter(region)) continue;

            var cx = output[0 * anchors + a];
            var cy = output[1 * anchors + a];
            var w = output[2 * anchors + a];
            var h = output[3 * anchors + a];
            var box = transform.MapCenterBoxToSource(cx, cy, w, h);
            if (box.IsEmpty) continue;

            candidates.Add(new Detection(region, bestScore, box));
        }

        return NonMaximumSuppression.Suppress(candidates, nmsIou);
    }

    /// <summary>
    /// Decodes a standard Ultralytics YOLOv8 detection export shaped
    /// [1, 4 + classes, anchors]. Only the requested class is returned.
    /// </summary>
    public static IReadOnlyList<Detection> DecodeYoloClass(
        ReadOnlySpan<float> output,
        int channels,
        int anchors,
        int classIndex,
        LetterboxTransform transform,
        double confidenceThreshold,
        double nmsIou)
    {
        if (channels < 5 || anchors <= 0 || classIndex < 0 || classIndex >= channels - 4)
            return Array.Empty<Detection>();
        if (output.Length < channels * anchors)
            return Array.Empty<Detection>();

        var candidates = new List<Detection>(16);
        for (var a = 0; a < anchors; a++)
        {
            var score = output[(4 + classIndex) * anchors + a];
            if (score < confidenceThreshold) continue;

            var cx = output[0 * anchors + a];
            var cy = output[1 * anchors + a];
            var w = output[2 * anchors + a];
            var h = output[3 * anchors + a];
            var box = transform.MapCenterBoxToSource(cx, cy, w, h);
            if (!box.IsEmpty)
                candidates.Add(new Detection(BodyRegion.Person, score, box));
        }

        return NonMaximumSuppression.Suppress(candidates, nmsIou);
    }
}

public static class NonMaximumSuppression
{
    public static IReadOnlyList<Detection> Suppress(IReadOnlyList<Detection> detections, double iouThreshold)
    {
        if (detections.Count <= 1) return detections;
        var result = new List<Detection>(detections.Count);
        foreach (var group in detections.GroupBy(d => d.Region))
        {
            var ordered = group.OrderByDescending(d => d.Confidence).ToList();
            while (ordered.Count > 0)
            {
                var best = ordered[0];
                result.Add(best);
                ordered.RemoveAt(0);
                for (var i = ordered.Count - 1; i >= 0; i--)
                {
                    if (best.Box.IoU(ordered[i].Box) >= iouThreshold)
                        ordered.RemoveAt(i);
                }
            }
        }
        return result;
    }
}

public static class SegmentationDecoder
{
    /// <summary>
    /// Converts selfie-segmentation output to an 8-bit mask. The bundled graph
    /// already ends in Sigmoid (its output node is <c>alphas</c>), so values are
    /// probabilities, not logits. Applying sigmoid twice would turn a confident
    /// background into a false positive.
    /// </summary>
    public static byte[] ProbabilitiesToMask(ReadOnlySpan<float> probabilities, int width, int height)
    {
        if (width <= 0 || height <= 0 || probabilities.Length < width * height)
            return Array.Empty<byte>();

        var mask = new byte[width * height];
        for (var i = 0; i < mask.Length; i++)
        {
            var p = Math.Clamp(probabilities[i], 0f, 1f);
            mask[i] = (byte)Math.Clamp((int)Math.Round(p * 255.0), 0, 255);
        }
        return mask;
    }

    /// <summary>
    /// Removes speckle and keeps the largest connected component. Keeps masks
    /// conservative: a small amount of surrounding area is retained rather than
    /// eroding the subject and exposing skin.
    /// </summary>
    public static byte[] CleanMask(ReadOnlySpan<byte> source, int width, int height, byte threshold = 128)
    {
        if (width <= 0 || height <= 0 || source.Length < width * height)
            return Array.Empty<byte>();

        var binary = new byte[width * height];
        for (var i = 0; i < binary.Length; i++) binary[i] = source[i] >= threshold ? (byte)1 : (byte)0;
        if (binary.All(v => v == 0)) return source.ToArray();

        // Dilate by one pixel to avoid cutting a thin edge.
        var dilated = new byte[binary.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (binary[y * width + x] == 0) continue;
                for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                    for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                        dilated[yy * width + xx] = 1;
            }
        }

        var visited = new bool[binary.Length];
        var queue = new Queue<int>();
        var best = new List<int>();
        var current = new List<int>();
        for (var start = 0; start < dilated.Length; start++)
        {
            if (dilated[start] == 0 || visited[start]) continue;
            current.Clear();
            queue.Clear();
            queue.Enqueue(start);
            visited[start] = true;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                current.Add(index);
                var x = index % width;
                var y = index / width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width) continue;
                        var ni = ny * width + nx;
                        if (dilated[ni] == 0 || visited[ni]) continue;
                        visited[ni] = true;
                        queue.Enqueue(ni);
                    }
                }
            }
            if (current.Count > best.Count) (best, current) = (current, best);
        }

        var result = new byte[width * height];
        foreach (var index in best)
        {
            var x = index % width;
            var y = index / width;
            for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                    result[yy * width + xx] = 255;
        }
        return result;
    }
}
