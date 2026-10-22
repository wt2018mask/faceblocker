using System.Globalization;

namespace VeilDesk.Core.Geometry;

/// <summary>
/// A rectangle expressed in physical pixels of one monitor. Coordinates are
/// monitor-local; origin (0,0) is that monitor's top-left corner even when the
/// monitor itself starts at a negative virtual-desktop coordinate.
/// </summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public static readonly PixelRect Empty = new(0, 0, 0, 0);

    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    public bool IsEmpty => Width <= 0.001 || Height <= 0.001;
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;

    public static PixelRect FromLtrb(double left, double top, double right, double bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public PixelRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public PixelRect Expand(double pixels)
        => new(X - pixels, Y - pixels, Width + pixels * 2, Height + pixels * 2);

    public PixelRect ExpandFraction(double fraction)
    {
        var dx = Width * fraction;
        var dy = Height * fraction;
        return new(X - dx, Y - dy, Width + dx * 2, Height + dy * 2);
    }

    public PixelRect Scale(double sx, double sy)
        => new(X * sx, Y * sy, Width * sx, Height * sy);

    public PixelRect Clip(PixelRect bounds)
    {
        var left = Math.Max(X, bounds.X);
        var top = Math.Max(Y, bounds.Y);
        var right = Math.Min(Right, bounds.Right);
        var bottom = Math.Min(Bottom, bounds.Bottom);
        return right <= left || bottom <= top ? Empty : FromLtrb(left, top, right, bottom);
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLtrb(
            Math.Min(X, other.X),
            Math.Min(Y, other.Y),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    public bool Contains(double x, double y)
        => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool Intersects(PixelRect other)
        => !(other.X >= Right || other.Right <= X || other.Y >= Bottom || other.Bottom <= Y);

    public double IntersectionArea(PixelRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w <= 0 || h <= 0 ? 0 : w * h;
    }

    public double IoU(PixelRect other)
    {
        var inter = IntersectionArea(other);
        if (inter <= 0) return 0;
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public double CenterDistance(PixelRect other)
    {
        var dx = CenterX - other.CenterX;
        var dy = CenterY - other.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Intersection over the smaller rectangle. Useful for a small body
    /// part detection sitting inside a large person box.</summary>
    public double OverlapOfSmaller(PixelRect other)
    {
        var inter = IntersectionArea(other);
        var min = Math.Min(Area, other.Area);
        return min <= 0 ? 0 : inter / min;
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:0.#},{Y:0.#},{Width:0.#}x{Height:0.#})");
}
