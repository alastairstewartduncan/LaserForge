namespace LaserForge.Core.Geometry;

/// <summary>
/// A flattened path: a sequence of points, optionally closed. All curves (beziers, arcs, ellipses, glyphs)
/// are flattened to polylines on import, which keeps the G-code generator simple and fast.
/// </summary>
public sealed class Polyline
{
    public List<Vec2> Points { get; set; } = new();
    public bool Closed { get; set; }

    public Polyline() { }

    public Polyline(IEnumerable<Vec2> points, bool closed)
    {
        Points = points.ToList();
        Closed = closed;
    }

    public Bounds Bounds => Bounds.Of(Points);

    public Polyline Clone() => new(Points, Closed);

    public Polyline Transformed(Transform2D t) => new(Points.Select(t.Apply), Closed);

    /// <summary>Total drawn length including the closing segment.</summary>
    public double Length
    {
        get
        {
            double len = 0;
            for (int i = 1; i < Points.Count; i++) len += Vec2.Distance(Points[i - 1], Points[i]);
            if (Closed && Points.Count > 2) len += Vec2.Distance(Points[^1], Points[0]);
            return len;
        }
    }

    /// <summary>Signed area (shoelace). Only meaningful for closed paths.</summary>
    public double SignedArea
    {
        get
        {
            double a = 0;
            for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
                a += (Points[j].X * Points[i].Y) - (Points[i].X * Points[j].Y);
            return a / 2;
        }
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public bool ContainsPoint(Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var a = Points[i];
            var b = Points[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Minimum distance from a point to any segment of the path (used for click hit-testing of open paths).</summary>
    public double DistanceTo(Vec2 p)
    {
        if (Points.Count == 0) return double.PositiveInfinity;
        if (Points.Count == 1) return Vec2.Distance(p, Points[0]);
        double best = double.PositiveInfinity;
        int n = Closed ? Points.Count : Points.Count - 1;
        for (int i = 0; i < n; i++)
            best = Math.Min(best, SegmentDistance(p, Points[i], Points[(i + 1) % Points.Count]));
        return best;
    }

    private static double SegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 < 1e-12) return Vec2.Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return Vec2.Distance(p, a + ab * t);
    }
}
