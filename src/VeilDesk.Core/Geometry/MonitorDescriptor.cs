namespace VeilDesk.Core.Geometry;

/// <summary>
/// A display known to the capture layer. Bounds are virtual-desktop physical
/// pixels. X/Y may be negative on secondary displays to the left or above the
/// primary display.
/// </summary>
public sealed record MonitorDescriptor(
    string Id,
    string DeviceName,
    string FriendlyName,
    int X,
    int Y,
    int Width,
    int Height,
    double DpiScale,
    bool IsPrimary)
{
    public PixelRect Bounds => new(X, Y, Width, Height);
    public PixelRect LocalBounds => new(0, 0, Width, Height);
    public double AspectRatio => Height <= 0 ? 0 : (double)Width / Height;

    public override string ToString()
        => $"{FriendlyName} ({Width}x{Height} @ {DpiScale:0.##}x, {X},{Y})";
}
