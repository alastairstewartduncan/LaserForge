namespace AsdLasercraft.Core.Geometry;

/// <summary>Axis-aligned bounding box in millimetres.</summary>
public readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public static readonly Bounds Empty = new(double.PositiveInfinity, double.PositiveInfinity,
                                              double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;
    public Vec2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);
    public double Area => Width * Height;

    public static Bounds FromRect(double x, double y, double w, double h) => new(x, y, x + w, y + h);

    public Bounds Include(Vec2 p) => new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

    public Bounds Union(Bounds o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
    }

    public bool Contains(Vec2 p, double tolerance = 0) =>
        p.X >= MinX - tolerance && p.X <= MaxX + tolerance && p.Y >= MinY - tolerance && p.Y <= MaxY + tolerance;

    public bool Contains(Bounds o) => !o.IsEmpty && o.MinX >= MinX && o.MaxX <= MaxX && o.MinY >= MinY && o.MaxY <= MaxY;

    public bool Intersects(Bounds o) => !IsEmpty && !o.IsEmpty &&
        o.MinX <= MaxX && o.MaxX >= MinX && o.MinY <= MaxY && o.MaxY >= MinY;

    public static Bounds Of(IEnumerable<Vec2> points)
    {
        var b = Empty;
        foreach (var p in points) b = b.Include(p);
        return b;
    }
}
