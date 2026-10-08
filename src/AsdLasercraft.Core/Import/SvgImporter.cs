using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Import;

/// <summary>
/// Lightweight SVG importer: path, rect, circle, ellipse, line, polyline, polygon, nested groups and
/// transforms. Output is flattened polylines in millimetres. Text elements are not imported (convert
/// text to paths in your design tool first).
/// </summary>
public static class SvgImporter
{
    private const double PxToMm = 25.4 / 96.0;

    public static List<Polyline> Import(string svgText)
    {
        var doc = XDocument.Parse(svgText);
        var root = doc.Root ?? throw new FormatException("Empty SVG");
        var rootTransform = RootTransform(root);
        var result = new List<Polyline>();
        Walk(root, rootTransform, result);
        return result.Where(p => p.Points.Count >= 2).ToList();
    }

    /// <summary>Maps the viewBox to physical size (width/height with units), defaulting to 96 DPI px.</summary>
    private static Transform2D RootTransform(XElement svg)
    {
        double? w = Length(svg.Attribute("width")?.Value);
        double? h = Length(svg.Attribute("height")?.Value);
        var vb = svg.Attribute("viewBox")?.Value;
        if (vb != null)
        {
            var n = Numbers(vb);
            if (n.Count == 4 && n[2] > 0 && n[3] > 0)
            {
                double sx = (w ?? n[2] * PxToMm) / n[2];
                double sy = (h ?? n[3] * PxToMm) / n[3];
                // preserveAspectRatio default (xMidYMid meet) – use uniform scale.
                double s = Math.Min(sx, sy);
                return Transform2D.Translate(-n[0], -n[1]).Then(Transform2D.Scale(s, s));
            }
        }
        return Transform2D.Scale(PxToMm, PxToMm);
    }

    private static void Walk(XElement el, Transform2D parent, List<Polyline> output)
    {
        string name = el.Name.LocalName;
        if (name is "defs" or "clipPath" or "mask" or "symbol" or "style" or "metadata" or "title" or "desc") return;
        if (string.Equals(Attr(el, "display"), "none", StringComparison.OrdinalIgnoreCase)) return;

        var t = ParseTransform(el.Attribute("transform")?.Value).Then(parent);
        List<Polyline>? local = name switch
        {
            "path" => ParsePath(el.Attribute("d")?.Value ?? ""),
            "rect" => Rect(el),
            "circle" => Circle(el),
            "ellipse" => Ellipse(el),
            "line" => new() { new Polyline(new[] { new Vec2(D(el, "x1"), D(el, "y1")), new Vec2(D(el, "x2"), D(el, "y2")) }, false) },
            "polyline" => new() { PointsList(el, false) },
            "polygon" => new() { PointsList(el, true) },
            _ => null,
        };
        if (local != null) output.AddRange(local.Select(p => p.Transformed(t)));

        foreach (var child in el.Elements()) Walk(child, t, output);
    }

    private static string? Attr(XElement el, string name)
    {
        var direct = el.Attribute(name)?.Value;
        if (direct != null) return direct;
        var style = el.Attribute("style")?.Value;
        if (style == null) return null;
        foreach (var decl in style.Split(';'))
        {
            var kv = decl.Split(':', 2);
            if (kv.Length == 2 && kv[0].Trim() == name) return kv[1].Trim();
        }
        return null;
    }

    private static double D(XElement el, string attr) => Length(el.Attribute(attr)?.Value) is { } mm ? mm / PxToMm : 0;

    private static List<Polyline> Rect(XElement el)
    {
        double x = D(el, "x"), y = D(el, "y"), w = D(el, "width"), h = D(el, "height");
        if (w <= 0 || h <= 0) return new();
        double rx = D(el, "rx"), ry = D(el, "ry");
        if (rx <= 0 && ry <= 0)
            return new() { new Polyline(new[] { new Vec2(x, y), new Vec2(x + w, y), new Vec2(x + w, y + h), new Vec2(x, y + h) }, true) };
        if (rx <= 0) rx = ry; if (ry <= 0) ry = rx;
        rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
        var d = FormattableString.Invariant(
            $"M{x + rx},{y} H{x + w - rx} A{rx},{ry} 0 0 1 {x + w},{y + ry} V{y + h - ry} A{rx},{ry} 0 0 1 {x + w - rx},{y + h} H{x + rx} A{rx},{ry} 0 0 1 {x},{y + h - ry} V{y + ry} A{rx},{ry} 0 0 1 {x + rx},{y} Z");
        return ParsePath(d);
    }

    private static List<Polyline> Circle(XElement el)
    {
        double r = D(el, "r");
        return r <= 0 ? new() : new() { new Polyline(Curves.Ellipse(new Vec2(D(el, "cx"), D(el, "cy")), r, r, 0.01), true) };
    }

    private static List<Polyline> Ellipse(XElement el)
    {
        double rx = D(el, "rx"), ry = D(el, "ry");
        return rx <= 0 || ry <= 0 ? new() : new() { new Polyline(Curves.Ellipse(new Vec2(D(el, "cx"), D(el, "cy")), rx, ry, 0.01), true) };
    }

    private static Polyline PointsList(XElement el, bool closed)
    {
        var n = Numbers(el.Attribute("points")?.Value ?? "");
        var pts = new List<Vec2>();
        for (int i = 0; i + 1 < n.Count; i += 2) pts.Add(new Vec2(n[i], n[i + 1]));
        return new Polyline(pts, closed);
    }

    // ------------------------------------------------------------------ path data

    private static readonly Regex Token = new(@"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    public static List<Polyline> ParsePath(string d)
    {
        var tokens = Token.Matches(d).Select(m => m.Value).ToList();
        var paths = new List<Polyline>();
        List<Vec2>? cur = null;
        Vec2 pos = Vec2.Zero, start = Vec2.Zero, lastCtrl = Vec2.Zero;
        char cmd = ' ', prevCmd = ' ';
        int i = 0;
        const double tol = 0.05; // in user units; refined by later scaling is acceptable for laser work

        double Num() => double.Parse(tokens[i++], CultureInfo.InvariantCulture);
        void Flush(bool closed)
        {
            if (cur != null && cur.Count >= 2) paths.Add(new Polyline(cur, closed));
            cur = null;
        }
        void LineTo(Vec2 p) { cur ??= new List<Vec2> { pos }; cur.Add(p); pos = p; }

        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) cmd = tokens[i++][0];
            else if (cmd is ' ' or 'Z' or 'z') { i++; continue; } // stray numbers: skip rather than loop forever

            bool rel = char.IsLower(cmd);
            Vec2 Rel(double x, double y) => rel ? new Vec2(pos.X + x, pos.Y + y) : new Vec2(x, y);

            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    Flush(false);
                    pos = Rel(Num(), Num());
                    start = pos;
                    cur = new List<Vec2> { pos };
                    cmd = rel ? 'l' : 'L'; // subsequent pairs are implicit line-tos
                    break;
                case 'L': LineTo(Rel(Num(), Num())); break;
                case 'H': { double x = Num(); LineTo(new Vec2(rel ? pos.X + x : x, pos.Y)); break; }
                case 'V': { double y = Num(); LineTo(new Vec2(pos.X, rel ? pos.Y + y : y)); break; }
                case 'C':
                {
                    var c1 = Rel(Num(), Num()); var c2 = Rel(Num(), Num()); var p = Rel(Num(), Num());
                    cur ??= new List<Vec2> { pos };
                    cur.AddRange(Curves.Cubic(pos, c1, c2, p, tol));
                    lastCtrl = c2; pos = p;
                    break;
                }
                case 'S':
                {
                    var c1 = "CcSs".Contains(prevCmd) ? pos * 2 - lastCtrl : pos;
                    var c2 = Rel(Num(), Num()); var p = Rel(Num(), Num());
                    cur ??= new List<Vec2> { pos };
                    cur.AddRange(Curves.Cubic(pos, c1, c2, p, tol));
                    lastCtrl = c2; pos = p;
                    break;
                }
                case 'Q':
                {
                    var c = Rel(Num(), Num()); var p = Rel(Num(), Num());
                    cur ??= new List<Vec2> { pos };
                    cur.AddRange(Curves.Quadratic(pos, c, p, tol));
                    lastCtrl = c; pos = p;
                    break;
                }
                case 'T':
                {
                    var c = "QqTt".Contains(prevCmd) ? pos * 2 - lastCtrl : pos;
                    var p = Rel(Num(), Num());
                    cur ??= new List<Vec2> { pos };
                    cur.AddRange(Curves.Quadratic(pos, c, p, tol));
                    lastCtrl = c; pos = p;
                    break;
                }
                case 'A':
                {
                    double rx = Num(), ry = Num(), rot = Num();
                    bool large = Num() != 0, sweep = Num() != 0;
                    var p = Rel(Num(), Num());
                    cur ??= new List<Vec2> { pos };
                    cur.AddRange(Curves.SvgArc(pos, rx, ry, rot, large, sweep, p, tol));
                    pos = p;
                    break;
                }
                case 'Z':
                    if (cur != null && cur.Count > 1 && Vec2.Distance(cur[^1], start) < 1e-9) cur.RemoveAt(cur.Count - 1);
                    Flush(true);
                    pos = start;
                    break;
                default:
                    i++;
                    break;
            }
            prevCmd = cmd;
        }
        Flush(false);
        return paths;
    }

    // ------------------------------------------------------------------ helpers

    private static readonly Regex TransformRegex = new(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)", RegexOptions.Compiled);

    public static Transform2D ParseTransform(string? s)
    {
        var t = Transform2D.Identity;
        if (string.IsNullOrWhiteSpace(s)) return t;
        // SVG lists transforms outermost-first, so the right-most one is applied to the points first.
        var items = TransformRegex.Matches(s).Select(m => (m.Groups[1].Value, Numbers(m.Groups[2].Value))).ToList();
        for (int k = items.Count - 1; k >= 0; k--)
        {
            var (op, n) = items[k];
            Transform2D step = op switch
            {
                "matrix" when n.Count == 6 => new Transform2D(n[0], n[1], n[2], n[3], n[4], n[5]),
                "translate" => Transform2D.Translate(n.ElementAtOrDefault(0), n.ElementAtOrDefault(1)),
                "scale" => Transform2D.Scale(n.ElementAtOrDefault(0), n.Count > 1 ? n[1] : n.ElementAtOrDefault(0)),
                "rotate" when n.Count >= 3 => Transform2D.Rotate(n[0], new Vec2(n[1], n[2])),
                "rotate" => Transform2D.Rotate(n.ElementAtOrDefault(0)),
                "skewX" => Transform2D.SkewX(n.ElementAtOrDefault(0)),
                "skewY" => Transform2D.SkewY(n.ElementAtOrDefault(0)),
                _ => Transform2D.Identity,
            };
            t = t.Then(step);
        }
        return t;
    }

    private static readonly Regex NumberRegex = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    private static List<double> Numbers(string s) =>
        NumberRegex.Matches(s).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();

    /// <summary>Parses an SVG length to millimetres. Unitless and px are 1/96 inch.</summary>
    public static double? Length(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-z%]*)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "mm" => v,
            "cm" => v * 10,
            "in" => v * 25.4,
            "pt" => v * 25.4 / 72,
            "pc" => v * 25.4 / 6,
            "%" => null,
            _ => v * PxToMm,
        };
    }
}
