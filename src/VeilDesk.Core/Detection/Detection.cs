using VeilDesk.Core.Geometry;

namespace VeilDesk.Core.Detection;

/// <summary>
/// One model prediction in monitor-local physical pixels. Mask data, when
/// present, is an 8-bit coverage map whose dimensions are MaskWidth x MaskHeight
/// and which maps onto <see cref="Box"/>.
/// </summary>
public sealed record Detection(
    BodyRegion Region,
    double Confidence,
    PixelRect Box,
    byte[]? Mask = null,
    int MaskWidth = 0,
    int MaskHeight = 0)
{
    public bool HasMask => Mask is { Length: > 0 } && MaskWidth > 0 && MaskHeight > 0;
    public bool IsPerson => Region == BodyRegion.Person;
}
