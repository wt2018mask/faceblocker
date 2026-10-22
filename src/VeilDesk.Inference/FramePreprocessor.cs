using VeilDesk.Core.Geometry;
using VeilDesk.Core.Vision;

namespace VeilDesk.Inference;

/// <summary>
/// Converts BGRA desktop pixels to planar RGB float tensors. All resampling is
/// implemented locally so the runtime has no OpenCV/Pillow dependency.
///
/// The mapping is: model pixel (mx,my) samples source pixel
/// ((mx - PadX) / ScaleX, (my - PadY) / ScaleY). Pixels outside SourceRect are
/// filled with <paramref name="fill"/>; this is what implements the models'
/// letterbox/padding conventions without allocating an intermediate image.
/// </summary>
public static unsafe class FramePreprocessor
{
    public const int NudeNetSize = 320;
    public const int SegmentationSize = 256;

    public static void PreprocessNudeNet(VideoFrame frame, float[] destination)
    {
        var max = Math.Max(frame.Width, frame.Height);
        var scale = (double)NudeNetSize / Math.Max(1, max);
        // NudeNet pads right/bottom to a square, then resizes the square.
        BgraToRgbNchw(
            frame.Span, frame.Width, frame.Height, frame.Stride,
            new PixelRect(0, 0, frame.Width, frame.Height),
            destination, NudeNetSize, NudeNetSize,
            scale, scale, 0, 0, fill: 0);
    }

    public static void PreprocessYolo(VideoFrame frame, int modelSize, float[] destination)
    {
        var scale = Math.Min((double)modelSize / Math.Max(1, frame.Width),
                             (double)modelSize / Math.Max(1, frame.Height));
        var padX = (modelSize - frame.Width * scale) / 2.0;
        var padY = (modelSize - frame.Height * scale) / 2.0;
        BgraToRgbNchw(
            frame.Span, frame.Width, frame.Height, frame.Stride,
            new PixelRect(0, 0, frame.Width, frame.Height),
            destination, modelSize, modelSize,
            scale, scale, padX, padY, fill: 114);
    }

    /// <summary>
    /// Crops a source-space box (normally a person box) and squashes it to the
    /// segmentation input. The resulting mask is stretched back over that same
    /// box, so the non-uniform scaling cancels exactly.
    /// </summary>
    public static void PreprocessSegmentation(VideoFrame frame, PixelRect box, float[] destination)
    {
        var clipped = box.Clip(new PixelRect(0, 0, frame.Width, frame.Height));
        if (clipped.IsEmpty || clipped.Width < 2 || clipped.Height < 2)
        {
            Array.Clear(destination);
            return;
        }

        BgraToRgbNchw(
            frame.Span, frame.Width, frame.Height, frame.Stride,
            clipped, destination, SegmentationSize, SegmentationSize,
            SegmentationSize / clipped.Width, SegmentationSize / clipped.Height,
            0, 0, fill: 0);
    }

    public static void BgraToRgbNchw(
        ReadOnlySpan<byte> bgra,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        PixelRect sourceRect,
        float[] destination,
        int modelWidth,
        int modelHeight,
        double scaleX,
        double scaleY,
        double padX,
        double padY,
        byte fill)
    {
        if (destination.Length < modelWidth * modelHeight * 3)
            throw new ArgumentException("Destination tensor is too small.", nameof(destination));
        if (scaleX <= 0 || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX), "Scale must be positive.");

        var plane = modelWidth * modelHeight;
        var fillValue = fill / 255f;
        var rectLeft = (int)Math.Floor(sourceRect.X);
        var rectTop = (int)Math.Floor(sourceRect.Y);
        var rectRight = (int)Math.Ceiling(sourceRect.Right);
        var rectBottom = (int)Math.Ceiling(sourceRect.Bottom);

        fixed (byte* srcBase = bgra)
        fixed (float* dstBase = destination)
        {
            var src = srcBase;
            var dstR = dstBase;
            var dstG = dstBase + plane;
            var dstB = dstBase + 2 * plane;

            for (var my = 0; my < modelHeight; my++)
            {
                var sy = (my - padY) / scaleY;
                var insideY = sy >= rectTop && sy <= rectBottom - 1;
                var y0 = insideY ? Math.Clamp((int)Math.Floor(sy), rectTop, rectBottom - 1) : 0;
                var y1 = insideY ? Math.Min(y0 + 1, rectBottom - 1) : 0;
                var fy = insideY ? (float)(sy - y0) : 0f;
                for (var mx = 0; mx < modelWidth; mx++)
                {
                    var index = my * modelWidth + mx;
                    var sx = (mx - padX) / scaleX;
                    var insideX = insideY && sx >= rectLeft && sx <= rectRight - 1;
                    if (!insideX)
                    {
                        dstR[index] = fillValue;
                        dstG[index] = fillValue;
                        dstB[index] = fillValue;
                        continue;
                    }

                    var x0 = Math.Clamp((int)Math.Floor(sx), rectLeft, rectRight - 1);
                    var x1 = Math.Min(x0 + 1, rectRight - 1);
                    var fx = (float)(sx - x0);
                    var i00 = y0 * sourceStride + x0 * 4;
                    var i10 = y0 * sourceStride + x1 * 4;
                    var i01 = y1 * sourceStride + x0 * 4;
                    var i11 = y1 * sourceStride + x1 * 4;
                    if (i11 + 2 >= bgra.Length)
                    {
                        dstR[index] = fillValue;
                        dstG[index] = fillValue;
                        dstB[index] = fillValue;
                        continue;
                    }

                    // BGRA in memory -> RGB in the tensor.
                    var b00 = src[i00];
                    var g00 = src[i00 + 1];
                    var r00 = src[i00 + 2];
                    var b10 = src[i10];
                    var g10 = src[i10 + 1];
                    var r10 = src[i10 + 2];
                    var b01 = src[i01];
                    var g01 = src[i01 + 1];
                    var r01 = src[i01 + 2];
                    var b11 = src[i11];
                    var g11 = src[i11 + 1];
                    var r11 = src[i11 + 2];

                    var w00 = (1 - fx) * (1 - fy);
                    var w10 = fx * (1 - fy);
                    var w01 = (1 - fx) * fy;
                    var w11 = fx * fy;

                    dstR[index] = (r00 * w00 + r10 * w10 + r01 * w01 + r11 * w11) * (1f / 255f);
                    dstG[index] = (g00 * w00 + g10 * w10 + g01 * w01 + g11 * w11) * (1f / 255f);
                    dstB[index] = (b00 * w00 + b10 * w10 + b01 * w01 + b11 * w11) * (1f / 255f);
                }
            }
        }
    }
}
