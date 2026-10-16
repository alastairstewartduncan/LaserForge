using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CorePolyline = AsdLasercraft.Core.Geometry.Polyline;
using Vec2 = AsdLasercraft.Core.Geometry.Vec2;

namespace AsdLasercraft.App.Services;

/// <summary>Converts text in any installed font to outline polylines (in mm) using WPF's glyph geometry.</summary>
public static class TextToPath
{
    public static List<CorePolyline> Build(string text, FontFamily family, double sizeMm, bool bold, bool italic, Vec2 origin)
    {
        var ft = Format(text, family, sizeMm, bold, italic);
        return Flatten(ft.BuildGeometry(new Point(origin.X, origin.Y)));
    }

    /// <summary>Outlines with the baseline on y = 0 and the start, middle or end of the text at x = 0.</summary>
    public static List<CorePolyline> BuildAnchored(string text, FontFamily family, double size, bool bold, bool italic, AsdLasercraft.Core.Import.TextAnchor anchor)
    {
        var ft = Format(text, family, size, bold, italic);
        double width = ft.WidthIncludingTrailingWhitespace;
        double x = anchor switch
        {
            AsdLasercraft.Core.Import.TextAnchor.Middle => -width / 2,
            AsdLasercraft.Core.Import.TextAnchor.End => -width,
            _ => 0,
        };
        return Flatten(ft.BuildGeometry(new Point(x, -ft.Baseline)));
    }

    private static FormattedText Format(string text, FontFamily family, double size, bool bold, bool italic)
    {
        var typeface = new Typeface(family, italic ? FontStyles.Italic : FontStyles.Normal,
                                    bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        // We build directly in millimetre units: em size = size, one "pixel" = one mm.
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
    }

    private static List<CorePolyline> Flatten(Geometry geometry)
    {
        var flat = geometry.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute);

        var result = new List<CorePolyline>();
        foreach (var fig in flat.Figures)
        {
            var pts = new List<Vec2> { new(fig.StartPoint.X, fig.StartPoint.Y) };
            foreach (var seg in fig.Segments)
            {
                switch (seg)
                {
                    case LineSegment ls: pts.Add(new Vec2(ls.Point.X, ls.Point.Y)); break;
                    case PolyLineSegment pls: pts.AddRange(pls.Points.Select(p => new Vec2(p.X, p.Y))); break;
                }
            }
            if (pts.Count > 1 && pts[^1] == pts[0]) pts.RemoveAt(pts.Count - 1);
            if (pts.Count >= 2) result.Add(new CorePolyline(pts, fig.IsClosed || pts.Count > 2));
        }
        return result;
    }
}

/// <summary>Real-font text outlining for the AI design and assistant tools.</summary>
public sealed class WpfTextOutliner : AsdLasercraft.Core.Import.ITextOutliner
{
    public static readonly WpfTextOutliner Instance = new();

    public List<CorePolyline> Outline(string text, string fontFamily, double size, bool bold, bool italic, AsdLasercraft.Core.Import.TextAnchor anchor)
    {
        // Must run on a thread that can create WPF text objects; the callers marshal to the UI thread.
        var family = new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Arial" : fontFamily);
        return TextToPath.BuildAnchored(text, family, size, bold, italic, anchor);
    }
}
