using System.Buffers;
using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Vision;

/// <summary>
/// One monitor frame in BGRA8, top-down, monitor-local physical pixels.
/// Buffers are rented from the shared pool: a frame must be disposed exactly
/// once by its final consumer. Capture publishes into a <see cref="FrameMailbox"/>
/// which owns the latest frame and drops (disposes) older ones.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private byte[]? _pixels;
    private int _refCount = 1;

    private VideoFrame(
        byte[] pixels,
        int width,
        int height,
        int stride,
        long frameNumber,
        long timestampMs,
        string monitorId,
        PixelRect monitorBounds,
        DateTime capturedAtUtc)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        FrameNumber = frameNumber;
        TimestampMs = timestampMs;
        MonitorId = monitorId;
        MonitorBounds = monitorBounds;
        CapturedAtUtc = capturedAtUtc;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long FrameNumber { get; }
    public long TimestampMs { get; }
    public string MonitorId { get; }
    public PixelRect MonitorBounds { get; }
    public DateTime CapturedAtUtc { get; }
    public bool IsDisposed => Volatile.Read(ref _pixels) is null;

    public byte[] Pixels => Volatile.Read(ref _pixels)
        ?? throw new ObjectDisposedException(nameof(VideoFrame));

    /// <summary>
    /// Adds a reference so a capture session can keep the freshest frame alive
    /// while it is also published to the analysis mailbox. Every successful
    /// Retain must be paired with Dispose. Not safe to race against the final
    /// Dispose; the capture layer retains before dropping its own reference.
    /// </summary>
    public void Retain()
    {
        if (Volatile.Read(ref _pixels) is null) throw new ObjectDisposedException(nameof(VideoFrame));
        Interlocked.Increment(ref _refCount);
    }

    public ReadOnlySpan<byte> Span
    {
        get
        {
            var pixels = Pixels;
            return pixels.AsSpan(0, Stride * Height);
        }
    }

    public static VideoFrame Rent(
        int width,
        int height,
        int stride,
        long frameNumber,
        long timestampMs,
        string monitorId,
        PixelRect monitorBounds,
        DateTime? capturedAtUtc = null)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width * 4) throw new ArgumentOutOfRangeException(nameof(stride));
        var buffer = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        return new VideoFrame(buffer, width, height, stride, frameNumber, timestampMs,
            monitorId, monitorBounds, capturedAtUtc ?? DateTime.UtcNow);
    }

    public VideoFrame Clone()
    {
        var pixels = Pixels;
        var clone = Rent(Width, Height, Stride, FrameNumber, TimestampMs, MonitorId, MonitorBounds, CapturedAtUtc);
        pixels.AsSpan(0, Stride * Height).CopyTo(clone.Pixels);
        return clone;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) != 0) return;
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
    }
}

/// <summary>
/// Single-slot latest-frame mailbox. Capture never blocks on analysis: when a
/// newer frame arrives the older one is disposed, so inference always sees the
/// freshest evidence and latency cannot grow without bound.
/// </summary>
public sealed class FrameMailbox
{
    private VideoFrame? _latest;
    private long _published;
    private long _dropped;

    public long Published => Interlocked.Read(ref _published);
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Publish(VideoFrame frame)
    {
        Interlocked.Increment(ref _published);
        var old = Interlocked.Exchange(ref _latest, frame);
        if (old is not null)
        {
            Interlocked.Increment(ref _dropped);
            old.Dispose();
        }
    }

    public VideoFrame? TakeLatest() => Interlocked.Exchange(ref _latest, null);

    public void Clear()
    {
        var old = Interlocked.Exchange(ref _latest, null);
        old?.Dispose();
    }
}
