using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Tools;

public sealed class TraceOptions
{
    /// <summary>Pixels darker than this (after the image's brightness/contrast/gamma/invert) are traced.</summary>
    public byte Threshold { get; set; } = 128;

    /// <summary>Maximum distance a simplified outline may stray from the pixel edge, mm. 0 = auto (about one pixel).</summary>
    public double SmoothingMm { get; set; }

    /// <summary>Outlines enclosing less than this area are dropped as specks, mm².</summary>
    public double MinAreaMm2 { get; set; } = 0.25;

    /// <summary>Longer image side is reduced to this many pixels before tracing, to keep it quick.</summary>
    public int MaxSidePixels { get; set; } = 1200;
}

/// <summary>
/// Turns a bitmap into vector outlines ("trace image"): threshold, follow the boundary between dark and light
/// pixels into closed loops, then simplify. Holes come out as separate loops, so the result fills correctly
/// with the even-odd rule and can be cut or engraved like any drawn shape.
/// </summary>
public static class ImageTracer
{
    public static List<Polyline> Trace(ImageShape img, TraceOptions? options = null)
    {
        var o = options ?? new TraceOptions();
        var result = new List<Polyline>();
        if (img.PixelWidth <= 0 || img.PixelHeight <= 0 || img.Gray.Length < img.PixelWidth * img.PixelHeight) return result;

        var adjusted = ImageProcessor.Adjust(img.Gray, img.Brightness, img.Contrast, img.Gamma, img.Invert);
        int w = img.PixelWidth, h = img.PixelHeight;
        double scale = Math.Min(1.0, (double)o.MaxSidePixels / Math.Max(w, h));
        if (scale < 1)
        {
            int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
            adjusted = ImageProcessor.Resample(adjusted, w, h, nw, nh);
            w = nw; h = nh;
        }

        var dark = new bool[w * h];
        for (int i = 0; i < dark.Length; i++) dark[i] = adjusted[i] < o.Threshold;

        double pxW = img.WidthMm / w, pxH = img.HeightMm / h;
        double tolerance = o.SmoothingMm > 0 ? o.SmoothingMm : Math.Max(0.02, 0.75 * Math.Max(pxW, pxH));
        double pixelArea = pxW * pxH;

        foreach (var loop in TraceLoops(dark, w, h))
        {
            // Pixel-grid area check first: cheap rejection of single-pixel noise.
            var mm = loop.Select(p => new Vec2(img.X + p.X * pxW, img.Y + p.Y * pxH)).ToList();
            double area = Math.Abs(PathOffset.SignedArea(mm));
            if (area < Math.Max(o.MinAreaMm2, pixelArea * 0.5)) continue;
            var simple = SimplifyClosed(mm, tolerance);
            if (simple.Count >= 3) result.Add(new Polyline(simple, true));
        }
        return result;
    }

    // ------------------------------------------------------------------ boundary following

    private readonly record struct Edge(int X0, int Y0, int X1, int Y1);

    /// <summary>
    /// Builds directed "crack" edges between dark and light pixels with dark always on the right (screen, Y-down),
    /// then chains them into loops. At a vertex shared by two diagonal dark pixels the walk turns right, so
    /// diagonal touches are kept as separate shapes.
    /// </summary>
    public static List<List<Vec2>> TraceLoops(bool[] dark, int w, int h)
    {
        bool D(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && dark[y * w + x];

        var edges = new List<Edge>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!dark[y * w + x]) continue;
                if (!D(x, y - 1)) edges.Add(new Edge(x, y, x + 1, y));             // top, heading east
                if (!D(x + 1, y)) edges.Add(new Edge(x + 1, y, x + 1, y + 1));     // right, heading south
                if (!D(x, y + 1)) edges.Add(new Edge(x + 1, y + 1, x, y + 1));     // bottom, heading west
                if (!D(x - 1, y)) edges.Add(new Edge(x, y + 1, x, y));             // left, heading north
            }

        long Key(int x, int y) => (long)y * (w + 2) + x;
        var byStart = new Dictionary<long, List<int>>(edges.Count);
        for (int i = 0; i < edges.Count; i++)
        {
            long k = Key(edges[i].X0, edges[i].Y0);
            if (!byStart.TryGetValue(k, out var list)) byStart[k] = list = new List<int>(2);
            list.Add(i);
        }

        var used = new bool[edges.Count];
        var loops = new List<List<Vec2>>();
        for (int start = 0; start < edges.Count; start++)
        {
            if (used[start]) continue;
            var pts = new List<Vec2>();
            int cur = start;
            int guard = edges.Count + 4;
            while (guard-- > 0)
            {
                used[cur] = true;
                var e = edges[cur];
                pts.Add(new Vec2(e.X0, e.Y0));
                int dx = e.X1 - e.X0, dy = e.Y1 - e.Y0;
                if (e.X1 == edges[start].X0 && e.Y1 == edges[start].Y0) break; // back where we began
                if (!byStart.TryGetValue(Key(e.X1, e.Y1), out var next)) break;
                int chosen = -1;
                if (next.Count == 1) chosen = used[next[0]] ? -1 : next[0];
                else
                {
                    // Prefer a right turn, then straight on, then a left turn.
                    var prefs = new[] { (-dy, dx), (dx, dy), (dy, -dx) };
                    foreach (var (px, py) in prefs)
                    {
                        foreach (int cand in next)
                        {
                            if (used[cand]) continue;
                            var c = edges[cand];
                            if (c.X1 - c.X0 == px && c.Y1 - c.Y0 == py) { chosen = cand; break; }
                        }
                        if (chosen >= 0) break;
                    }
                }
                if (chosen < 0) break; // loop closed (or, defensively, a dead end)
                cur = chosen;
            }
            var cleaned = RemoveCollinear(pts);
            if (cleaned.Count >= 3) loops.Add(cleaned);
        }
        return loops;
    }

    private static List<Vec2> RemoveCollinear(List<Vec2> pts)
    {
        var r = new List<Vec2>(pts.Count);
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            var a = pts[(i - 1 + n) % n];
            var b = pts[i];
            var c = pts[(i + 1) % n];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) > 1e-12) r.Add(b);
        }
        return r;
    }

    // ------------------------------------------------------------------ simplification (Ramer–Douglas–Peucker)

    public static List<Vec2> SimplifyClosed(List<Vec2> pts, double tolerance)
    {
        if (pts.Count < 4 || tolerance <= 0) return pts;
        // Split the loop at the vertex farthest from the first so each half is an open polyline.
        int far = 0;
        double best = -1;
        for (int i = 1; i < pts.Count; i++)
        {
            double d = Vec2.Distance(pts[0], pts[i]);
            if (d > best) { best = d; far = i; }
        }
        var first = SimplifyOpen(pts.GetRange(0, far + 1), tolerance);
        var second = SimplifyOpen(pts.GetRange(far, pts.Count - far).Append(pts[0]).ToList(), tolerance);
        var result = new List<Vec2>(first);
        result.AddRange(second.Skip(1).Take(second.Count - 2));
        return result;
    }

    public static List<Vec2> SimplifyOpen(List<Vec2> pts, double tolerance)
    {
        if (pts.Count < 3) return new List<Vec2>(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double maxD = 0;
            int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = PerpDistance(pts[i], pts[a], pts[b]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > tolerance)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }
        var r = new List<Vec2>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) r.Add(pts[i]);
        return r;
    }

    private static double PerpDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len = ab.Length;
        if (len < 1e-12) return Vec2.Distance(p, a);
        return Math.Abs(ab.X * (a.Y - p.Y) - (a.X - p.X) * ab.Y) / len;
    }
}
