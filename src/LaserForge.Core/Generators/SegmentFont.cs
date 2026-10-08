using LaserForge.Core.Geometry;

namespace LaserForge.Core.Generators;

/// <summary>
/// Tiny seven-segment stroke font for labelling test grids without needing system fonts.
/// Supports digits, '.', '%' (approximate) and '-'.
/// </summary>
public static class SegmentFont
{
    //  aaa
    // f   b
    //  ggg
    // e   c
    //  ddd
    private static readonly Dictionary<char, string> Map = new()
    {
        ['0'] = "abcdef", ['1'] = "bc", ['2'] = "abged", ['3'] = "abgcd", ['4'] = "fgbc",
        ['5'] = "afgcd", ['6'] = "afgedc", ['7'] = "abc", ['8'] = "abcdefg", ['9'] = "abcdfg",
        ['-'] = "g", [' '] = "",
    };

    /// <summary>Returns stroke polylines for the text; height in mm, top-left at (x, y).</summary>
    public static List<Polyline> Render(string text, double x, double y, double height)
    {
        double w = height * 0.5, gap = height * 0.25;
        var result = new List<Polyline>();
        double cx = x;
        foreach (char ch in text)
        {
            if (ch == '.')
            {
                result.Add(Seg(new Vec2(cx, y + height), new Vec2(cx + height * 0.05, y + height)));
                cx += gap;
                continue;
            }
            if (Map.TryGetValue(ch, out var segs))
            {
                var tl = new Vec2(cx, y); var tr = new Vec2(cx + w, y);
                var ml = new Vec2(cx, y + height / 2); var mr = new Vec2(cx + w, y + height / 2);
                var bl = new Vec2(cx, y + height); var br = new Vec2(cx + w, y + height);
                foreach (char s in segs)
                {
                    result.Add(s switch
                    {
                        'a' => Seg(tl, tr), 'b' => Seg(tr, mr), 'c' => Seg(mr, br), 'd' => Seg(bl, br),
                        'e' => Seg(ml, bl), 'f' => Seg(tl, ml), _ => Seg(ml, mr),
                    });
                }
            }
            cx += w + gap;
        }
        return result;
    }

    public static double MeasureWidth(string text, double height)
    {
        double w = height * 0.5, gap = height * 0.25, total = 0;
        foreach (char ch in text) total += ch == '.' ? gap : w + gap;
        return Math.Max(0, total - gap);
    }

    private static Polyline Seg(Vec2 a, Vec2 b) => new(new[] { a, b }, false);
}
