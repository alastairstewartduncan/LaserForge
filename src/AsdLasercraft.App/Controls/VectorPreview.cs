using System.Windows;
using System.Windows.Media;
using AsdLasercraft.Core.Ai;
using CorePolyline = AsdLasercraft.Core.Geometry.Polyline;

namespace AsdLasercraft.App.Controls;

/// <summary>
/// Draws a generated design the way it will burn: filled areas solid (even-odd, so holes show), score lines as
/// thin strokes and cut outlines in red, on a material-coloured card with a millimetre size label.
/// </summary>
public sealed class VectorPreview : FrameworkElement
{
    private DesignResult? _design;

    public DesignResult? Design
    {
        get => _design;
        set { _design = value; InvalidateVisual(); }
    }

    /// <summary>Line art designs are scored, not filled.</summary>
    public DesignStyle RenderStyle { get; set; } = DesignStyle.Engrave;

    private static readonly Brush Material = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xD2, 0xA8)));
    private static readonly Brush Burn = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x24, 0x14)));
    private static readonly Pen BurnPen = FrozenPen(new Pen(Burn, 1.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen CutPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)), 1.4) { LineJoin = PenLineJoin.Round });

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, area);
        if (_design == null || _design.IsEmpty) return;

        var b = _design.Bounds;
        double pad = 28;
        double scale = Math.Min((area.Width - 2 * pad) / Math.Max(b.Width, 1e-6), (area.Height - 2 * pad - 18) / Math.Max(b.Height, 1e-6));
        if (scale <= 0 || double.IsInfinity(scale)) return;
        double ox = (area.Width - b.Width * scale) / 2 - b.MinX * scale;
        double oy = (area.Height - 18 - b.Height * scale) / 2 - b.MinY * scale;
        Point P(AsdLasercraft.Core.Geometry.Vec2 v) => new(ox + v.X * scale, oy + v.Y * scale);

        // Material card behind the design.
        var card = new Rect(ox + b.MinX * scale - 12, oy + b.MinY * scale - 12, b.Width * scale + 24, b.Height * scale + 24);
        dc.DrawRoundedRectangle(Material, null, card, 8, 8);

        Geometry Build(IEnumerable<CorePolyline> paths, bool filled)
        {
            var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = g.Open())
            {
                foreach (var p in paths)
                {
                    if (p.Points.Count < 2) continue;
                    ctx.BeginFigure(P(p.Points[0]), filled && p.Closed, p.Closed);
                    ctx.PolyLineTo(p.Points.Skip(1).Select(P).ToList(), true, true);
                }
            }
            g.Freeze();
            return g;
        }

        bool fill = RenderStyle != DesignStyle.LineArt;
        var filledPaths = fill ? _design.Closed.Concat(_design.Text).ToList() : new List<CorePolyline>();
        var strokedPaths = fill ? _design.Open : _design.Closed.Concat(_design.Open).Concat(_design.Text).ToList();
        if (filledPaths.Count > 0) dc.DrawGeometry(Burn, null, Build(filledPaths, true));
        if (strokedPaths.Count > 0) dc.DrawGeometry(null, BurnPen, Build(strokedPaths, false));
        if (_design.Cut.Count > 0) dc.DrawGeometry(null, CutPen, Build(_design.Cut, false));

        var label = new FormattedText($"{b.Width:0.#} × {b.Height:0.#} mm", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                      new Typeface("Segoe UI"), 12, (Brush)Application.Current.FindResource("Brush.TextMuted"), 1.0);
        dc.DrawText(label, new Point((area.Width - label.Width) / 2, card.Bottom + 6));
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }
}
