using LaserForge.Core.Geometry;

namespace LaserForge.Core.Gcode;

/// <summary>Even-odd scan-line hatching of closed polygons into horizontal burn segments.</summary>
public static class ScanlineFill
{
    /// <returns>Rows top-to-bottom; each row's segments are sorted left-to-right as (X0, X1).</returns>
    public static List<(double Y, List<(double X0, double X1)> Segments)> Hatch(IReadOnlyList<Polyline> closedPaths, double intervalMm)
    {
        intervalMm = Math.Max(intervalMm, 0.01);
        var bounds = Bounds.Empty;
        foreach (var p in closedPaths) bounds = bounds.Union(p.Bounds);
        var result = new List<(double, List<(double, double)>)>();
        if (bounds.IsEmpty) return result;

        // Pre-extract edges once.
        var edges = new List<(Vec2 A, Vec2 B)>();
        foreach (var path in closedPaths)
            for (int i = 0, j = path.Points.Count - 1; i < path.Points.Count; j = i++)
                if (Math.Abs(path.Points[i].Y - path.Points[j].Y) > 1e-12)
                    edges.Add((path.Points[j], path.Points[i]));

        var xs = new List<double>();
        for (double y = bounds.MinY + intervalMm / 2; y < bounds.MaxY; y += intervalMm)
        {
            xs.Clear();
            foreach (var (a, b) in edges)
            {
                // Half-open rule avoids double-counting shared vertices.
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            xs.Sort();
            var segs = new List<(double, double)>();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > 1e-6) segs.Add((xs[i], xs[i + 1]));
            result.Add((y, segs));
        }
        return result;
    }
}
