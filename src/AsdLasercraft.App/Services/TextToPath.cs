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
        var typeface = new Typeface(family, italic ? FontStyles.Italic : FontStyles.Normal,
                                    bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        // We build directly in millimetre units: em size = sizeMm, one "pixel" = one mm.
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                   typeface, sizeMm, Brushes.Black, 1.0);
        var geometry = ft.BuildGeometry(new Point(origin.X, origin.Y));
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
