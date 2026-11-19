using System.Globalization;
using System.Text;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Ai;

/// <summary>
/// Writes shapes from the project as a compact SVG in millimetres so a model can read and revise them.
/// Paths are simplified slightly and coordinates rounded to keep the text small.
/// </summary>
public static class SvgExporter
{
    /// <summary>More points than this is too much to send; the caller should ask the user to pick less.</summary>
    public const int MaxPoints = 6000;

    public sealed record Result(string Svg, Bounds Bounds, int PointCount);

    /// <summary>
    /// Exports the paths with their combined top-left moved to (0, 0). <paramref name="cut"/> paths go in &lt;g id="cut"&gt;.
    /// The tolerance grows automatically until the drawing fits within <see cref="MaxPoints"/> (or 0.3 mm is reached).
    /// </summary>
    public static Result Export(IEnumerable<Polyline> artwork, IEnumerable<Polyline>? cut = null, double toleranceMm = 0.03)
    {
        var art = artwork.Where(p => p.Points.Count >= 2).ToList();
        var cuts = (cut ?? Array.Empty<Polyline>()).Where(p => p.Points.Count >= 2).ToList();
        var b = Bounds.Empty;
        foreach (var p in art.Concat(cuts)) b = b.Union(p.Bounds);
        if (b.IsEmpty) return new Result("", b, 0);

        List<Polyline> sArt, sCut;
        int count;
        double tol = toleranceMm;
        while (true)
        {
            sArt = art.Select(p => Simplify(p, tol)).ToList();
            sCut = cuts.Select(p => Simplify(p, tol)).ToList();
            count = sArt.Concat(sCut).Sum(p => p.Points.Count);
            if (count <= MaxPoints || tol >= 0.3) break;
            tol *= 1.8;
        }

        var sb = new StringBuilder();
        string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(b.Width)}mm\" height=\"{N(b.Height)}mm\" viewBox=\"0 0 {N(b.Width)} {N(b.Height)}\">\n");
        void Write(Polyline p)
        {
            sb.Append("<path d=\"");
            for (int i = 0; i < p.Points.Count; i++)
                sb.Append(i == 0 ? 'M' : 'L').Append(N(p.Points[i].X - b.MinX)).Append(' ').Append(N(p.Points[i].Y - b.MinY));
            if (p.Closed) sb.Append('Z');
            sb.Append("\"/>\n");
        }
        foreach (var p in sArt) Write(p);
        if (sCut.Count > 0)
        {
            sb.Append("<g id=\"cut\">\n");
            foreach (var p in sCut) Write(p);
            sb.Append("</g>\n");
        }
        sb.Append("</svg>");
        return new Result(sb.ToString(), b, count);
    }

    /// <summary>A set of project shapes ready to send to a model: the SVG, where it sat on the bed, and how it burns.</summary>
    public sealed record ShapeExport(string Svg, Bounds Bounds, int PointCount, DesignStyle Style, List<VectorShape> Shapes)
    {
        public bool TooDetailed => PointCount > MaxPoints;
    }

    /// <summary>
    /// Exports the vector shapes among <paramref name="shapes"/>. Shapes on a cutting layer become the cut group;
    /// the style is worked out from the layers they sit on. Returns null when there is nothing vector to export.
    /// </summary>
    public static ShapeExport? FromShapes(LaserProject project, IEnumerable<Shape> shapes)
    {
        var vectors = shapes.OfType<VectorShape>().ToList();
        if (vectors.Count == 0) return null;
        var art = new List<Polyline>();
        var cut = new List<Polyline>();
        bool anyFill = false;
        foreach (var v in vectors)
        {
            var layer = project.FindLayer(v.LayerId);
            bool isCut = layer != null && layer.Mode == LayerMode.Line && Preflight.IsCut(layer);
            if (layer?.Mode == LayerMode.Fill) anyFill = true;
            (isCut ? cut : art).AddRange(v.Paths);
        }
        // A selection that is only cut lines is treated as line art rather than a cut group with nothing inside it.
        if (art.Count == 0) { art = cut; cut = new(); }
        var r = Export(art, cut);
        if (r.Svg.Length == 0) return null;
        var style = cut.Count > 0 && anyFill ? DesignStyle.EngraveAndCut : anyFill ? DesignStyle.Engrave : DesignStyle.LineArt;
        if (cut.Count > 0 && !anyFill) style = DesignStyle.LineArt;
        return new ShapeExport(r.Svg, r.Bounds, r.PointCount, style, vectors);
    }

    /// <summary>Ramer–Douglas–Peucker simplification; closed paths keep at least three points.</summary>
    public static Polyline Simplify(Polyline p, double tolerance)
    {
        var pts = p.Points;
        int min = p.Closed ? 3 : 2;
        if (pts.Count <= min || tolerance <= 0) return p.Clone();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, z) = stack.Pop();
            double max = 0;
            int index = -1;
            for (int i = a + 1; i < z; i++)
            {
                double d = Distance(pts[i], pts[a], pts[z]);
                if (d > max) { max = d; index = i; }
            }
            if (index >= 0 && max > tolerance)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, z));
            }
        }
        var result = pts.Where((_, i) => keep[i]).ToList();
        if (result.Count < min) return p.Clone();
        return new Polyline(result, p.Closed);
    }

    private static double Distance(Vec2 p, Vec2 a, Vec2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Vec2.Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Vec2.Distance(p, new Vec2(a.X + dx * t, a.Y + dy * t));
    }
}
