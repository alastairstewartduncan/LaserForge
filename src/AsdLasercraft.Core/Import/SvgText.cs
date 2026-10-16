using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Import;

public enum TextAnchor { Start, Middle, End }

/// <summary>
/// A run of text found in an SVG. Glyph space has the baseline start (or middle/end, per <see cref="Anchor"/>) at
/// the origin, Y down, in SVG user units; <see cref="Transform"/> maps that space to millimetres on the bed.
/// </summary>
public sealed record SvgText(string Text, string FontFamily, double FontSize, bool Bold, bool Italic, TextAnchor Anchor, Transform2D Transform);

/// <summary>
/// Turns text into outlines in glyph space: baseline on y = 0, anchored at x = 0, em size = <c>size</c>.
/// The desktop app implements this with real installed fonts.
/// </summary>
public interface ITextOutliner
{
    List<Polyline> Outline(string text, string fontFamily, double size, bool bold, bool italic, TextAnchor anchor);
}

/// <summary>
/// Font-free fallback (used by tests): one closed box per text run covering roughly where the glyphs would go.
/// The desktop app always supplies a real font outliner.
/// </summary>
public sealed class PlaceholderTextOutliner : ITextOutliner
{
    public static readonly PlaceholderTextOutliner Instance = new();

    public List<Polyline> Outline(string text, string fontFamily, double size, bool bold, bool italic, TextAnchor anchor)
    {
        double w = Math.Max(1, text.Length) * size * 0.55, h = size * 0.7;
        double x = anchor switch { TextAnchor.Middle => -w / 2, TextAnchor.End => -w, _ => 0 };
        return new() { new Polyline(new[] { new Vec2(x, -h), new Vec2(x + w, -h), new Vec2(x + w, 0), new Vec2(x, 0) }, true) };
    }
}

public static class SvgTextExtensions
{
    /// <summary>Outlines the text and places it on the bed (millimetres).</summary>
    public static List<Polyline> ToPolylines(this SvgText t, ITextOutliner outliner) =>
        outliner.Outline(t.Text, t.FontFamily, t.FontSize, t.Bold, t.Italic, t.Anchor)
            .Select(p => p.Transformed(t.Transform)).ToList();
}
