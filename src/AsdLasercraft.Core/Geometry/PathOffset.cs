namespace AsdLasercraft.Core.Geometry;

/// <summary>
/// Polygon offsetting for kerf compensation, plus helpers for walking along a path (tabs).
/// Designed for the small offsets used in laser cutting (a few tenths of a millimetre): mitred corners,
/// bevelled where the corner is very sharp so spikes don't shoot out.
/// </summary>
public static class PathOffset
{
    /// <summary>
    /// Offsets a closed path. Positive <paramref name="distance"/> grows the shape outwards,
    /// negative shrinks it. Open paths and degenerate shapes are returned unchanged.
    /// </summary>
    public static Polyline Offset(Polyline path, double distance)
    {
        if (!path.Closed || Math.Abs(distance) < 1e-9) return path.Clone();
        var pts = Dedupe(path.Points);
        if (pts.Count < 3) return path.Clone();

        double area = SignedArea(pts);
        if (Math.Abs(area) < 1e-12) return path.Clone();

        // With the standard shoelace sign, positive area means the left-hand normal (-dy, dx) points inwards.
        double outward = area > 0 ? -1 : 1;
        double d = distance;
        int n = pts.Count;
        var result = new List<Vec2>(n + 8);
        for (int i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n];
            var cur = pts[i];
            var next = pts[(i + 1) % n];
            var n1 = Normal(prev, cur) * outward;
            var n2 = Normal(cur, next) * outward;
            double cos = n1.X * n2.X + n1.Y * n2.Y;
            var bisector = n1 + n2;
            double bl = bisector.Length;
            if (cos < -0.5 || bl < 1e-9)
            {
                // Sharp corner (turn of more than 120°): bevel instead of a long mitre spike.
                result.Add(cur + n1 * d);
                result.Add(cur + n2 * d);
            }
            else
            {
                var m = bisector / bl;
                double dot = m.X * n1.X + m.Y * n1.Y;
                result.Add(cur + m * (d / Math.Max(dot, 1e-6)));
            }
        }
        return new Polyline(result, true);
    }

    /// <summary>Unit normal (-dy, dx) of the segment a→b.</summary>
    private static Vec2 Normal(Vec2 a, Vec2 b)
    {
        var e = b - a;
        double len = e.Length;
        return len < 1e-12 ? Vec2.Zero : new Vec2(-e.Y / len, e.X / len);
    }

    public static double SignedArea(IReadOnlyList<Vec2> pts)
    {
        double a = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            a += (pts[j].X * pts[i].Y) - (pts[i].X * pts[j].Y);
        return a / 2;
    }

    /// <summary>Removes consecutive duplicate points (and a closing point equal to the first).</summary>
    public static List<Vec2> Dedupe(IReadOnlyList<Vec2> points, double tolerance = 1e-6)
    {
        var r = new List<Vec2>(points.Count);
        foreach (var p in points)
            if (r.Count == 0 || Vec2.Distance(r[^1], p) > tolerance) r.Add(p);
        while (r.Count > 1 && Vec2.Distance(r[0], r[^1]) <= tolerance) r.RemoveAt(r.Count - 1);
        return r;
    }

    /// <summary>
    /// Splits a closed path into runs that alternate between burning and not burning, leaving
    /// <paramref name="tabCount"/> evenly spaced gaps of <paramref name="tabWidth"/> mm.
    /// The returned list starts and ends at the path's first point; each entry is (point, burn) where
    /// burn says whether the move <em>to</em> that point is cut.
    /// </summary>
    public static List<(Vec2 Point, bool Burn)> WalkWithTabs(Polyline closed, int tabCount, double tabWidth)
    {
        var pts = closed.Points.ToList();
        if (pts.Count > 0) pts.Add(pts[0]);
        var result = new List<(Vec2, bool)>();
        if (pts.Count < 3) return result;

        double perimeter = 0;
        for (int i = 1; i < pts.Count; i++) perimeter += Vec2.Distance(pts[i - 1], pts[i]);

        // Tabs only make sense if they leave most of the outline cut.
        tabWidth = Math.Max(0, tabWidth);
        var gaps = new List<(double Start, double End)>();
        if (tabCount > 0 && tabWidth > 0 && tabCount * tabWidth < perimeter * 0.5)
        {
            for (int k = 0; k < tabCount; k++)
            {
                double c = perimeter * (k + 0.5) / tabCount;
                gaps.Add((c - tabWidth / 2, c + tabWidth / 2));
            }
        }

        bool InGap(double s) => gaps.Any(g => s > g.Start && s < g.End);
        var splits = gaps.SelectMany(g => new[] { g.Start, g.End }).OrderBy(x => x).ToList();

        // Walk the outline; split each segment at every gap boundary inside it. Each piece burns
        // unless its midpoint lies inside a gap.
        result.Add((pts[0], false));
        double walked = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            double len = Vec2.Distance(a, b);
            if (len < 1e-12) continue;
            double segStart = walked, segEnd = walked + len, pieceStart = segStart;
            foreach (var s in splits)
            {
                if (s <= segStart + 1e-9 || s >= segEnd - 1e-9) continue;
                result.Add((Vec2.Lerp(a, b, (s - segStart) / len), !InGap((pieceStart + s) / 2)));
                pieceStart = s;
            }
            result.Add((b, !InGap((pieceStart + segEnd) / 2)));
            walked = segEnd;
        }
        return result;
    }
}
