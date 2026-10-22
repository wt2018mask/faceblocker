namespace VeilDesk.Core.Vision;

/// <summary>
/// A small grayscale thumbprint used for adaptive inference and scene-cut
/// detection. It never leaves the process and is never persisted.
/// </summary>
public sealed class SceneChangeDetector
{
    public const int ThumbWidth = 64;
    public const int ThumbHeight = 36;

    private byte[]? _previous;

    public byte[] ComputeThumbprint(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var thumb = new byte[ThumbWidth * ThumbHeight];
        if (width <= 0 || height <= 0 || bgra.Length < stride * height)
            return thumb;

        for (var ty = 0; ty < ThumbHeight; ty++)
        {
            var y0 = ty * height / ThumbHeight;
            var y1 = Math.Max(y0 + 1, (ty + 1) * height / ThumbHeight);
            for (var tx = 0; tx < ThumbWidth; tx++)
            {
                var x0 = tx * width / ThumbWidth;
                var x1 = Math.Max(x0 + 1, (tx + 1) * width / ThumbWidth);
                long sum = 0;
                long count = 0;
                for (var y = y0; y < y1; y += 2)
                {
                    var row = y * stride;
                    for (var x = x0; x < x1; x += 2)
                    {
                        var i = row + x * 4;
                        var b = bgra[i];
                        var g = bgra[i + 1];
                        var r = bgra[i + 2];
                        sum += (r * 77 + g * 150 + b * 29) >> 8;
                        count++;
                    }
                }
                thumb[ty * ThumbWidth + tx] = count == 0 ? (byte)0 : (byte)(sum / count);
            }
        }
        return thumb;
    }

    /// <summary>Returns normalized mean absolute difference in [0,1] and stores the thumbprint.</summary>
    public double Update(ReadOnlySpan<byte> thumbprint)
    {
        var delta = _previous is null ? 1.0 : Difference(_previous, thumbprint);
        _previous = thumbprint.ToArray();
        return delta;
    }

    public void Reset() => _previous = null;

    public static double Difference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var length = Math.Min(a.Length, b.Length);
        if (length == 0) return 0;
        long sum = 0;
        for (var i = 0; i < length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (length * 255.0);
    }
}
