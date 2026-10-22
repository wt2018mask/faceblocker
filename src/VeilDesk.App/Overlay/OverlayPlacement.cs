using VeilDesk.Core.Censoring;
using VeilDesk.Core.Geometry;

namespace VeilDesk.App.Overlay;

public sealed record RegionPlacement(CensorRegionKind Kind, PixelRect ExpectedDesktop, PixelRect ActualDesktop)
{
    public double MaximumEdgeError => new[]
    {
        Math.Abs(ExpectedDesktop.X - ActualDesktop.X), Math.Abs(ExpectedDesktop.Y - ActualDesktop.Y),
        Math.Abs(ExpectedDesktop.Right - ActualDesktop.Right), Math.Abs(ExpectedDesktop.Bottom - ActualDesktop.Bottom)
    }.Max();
}

public sealed record OverlayPlacement(string MonitorId, PixelRect ExpectedWindow, PixelRect ActualWindow,
    double DpiScale, IReadOnlyList<RegionPlacement> Regions);
