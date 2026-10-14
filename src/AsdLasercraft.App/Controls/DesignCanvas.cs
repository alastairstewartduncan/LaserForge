using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;
using Bounds = AsdLasercraft.Core.Geometry.Bounds;
using CoreShape = AsdLasercraft.Core.Model.Shape;
using Transform2D = AsdLasercraft.Core.Geometry.Transform2D;
using Vec2 = AsdLasercraft.Core.Geometry.Vec2;

namespace AsdLasercraft.App.Controls;

public enum DesignTool { Select, Rectangle, Ellipse, Line, Pen, Polygon, Text }

/// <summary>
/// The work area: draws the bed, rulers, grid, shapes and images in millimetre space with zoom/pan,
/// handles selection, moving, resizing and drawing new shapes, and shows the simulated preview and the
/// live laser head (with a glowing burn trail) while a job runs.
/// </summary>
public sealed class DesignCanvas : FrameworkElement
{
    // ------------------------------------------------------------------ public state

    public LaserProject? Project { get; set; }
    public HashSet<Guid> Selection { get; } = new();
    public Guid ActiveLayerId { get; set; }
    private bool _previewRaster;
    /// <summary>Show images as the exact dithered dots that will be burned, at the layer's line interval.</summary>
    public bool PreviewRaster
    {
        get => _previewRaster;
        set
        {
            _previewRaster = value;
            RenderOptions.SetBitmapScalingMode(this, value ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            InvalidateVisual();
        }
    }
    public bool SnapToGrid { get; set; } = true;
    public double SnapMm { get; set; } = 1.0;
    public int PolygonSides { get; set; } = 6;
    public bool ShowRulers { get; set; } = true;

    /// <summary>Laser head position in project space, shown as a crosshair.</summary>
    public Vec2? LaserPosition { get; set; }

    private bool _laserFiring;
    /// <summary>True while the laser is burning: the head glows and its path is drawn as a burn trail.</summary>
    public bool LaserFiring
    {
        get => _laserFiring;
        set
        {
            if (_laserFiring == value) return;
            _laserFiring = value;
            if (value) _pulseTimer.Start(); else _pulseTimer.Stop();
            InvalidateVisual();
        }
    }

    /// <summary>Where the job's anchor point will sit (current-position / user-origin jobs).</summary>
    public Vec2? JobAnchor { get; set; }

    /// <summary>Simulated preview data, or null when not previewing.</summary>
    public SimulationData? Simulation { get; set; }

    /// <summary>Preview playhead, seconds into the job.</summary>
    public double SimulationTime { get; set; }

    /// <summary>Draw laser-off travel moves in the preview.</summary>
    public bool ShowTravel { get; set; } = true;

    private DesignTool _tool;
    public DesignTool Tool
    {
        get => _tool;
        set { _tool = value; _penPoints.Clear(); Cursor = value == DesignTool.Select ? Cursors.Arrow : Cursors.Cross; InvalidateVisual(); }
    }

    /// <summary>Raised immediately before the canvas modifies the project (take an undo snapshot here).</summary>
    public event Action? BeforeChange;
    public event Action? Changed;
    public event Action? SelectionChanged;
    public event Action<Vec2>? CursorMoved;
    /// <summary>Raised when the Text tool is clicked; the handler shows a dialog and returns the new shape (or null).</summary>
    public event Func<Vec2, CoreShape?>? TextRequested;
    /// <summary>Raised when image/SVG/project files are dropped on the canvas, with the drop point in mm.</summary>
    public event Action<string[], Vec2>? FilesDropped;

    // ------------------------------------------------------------------ view transform

    private const double RulerPx = 22;
    private double _zoom = 2;          // screen px per mm
    private Vector _pan = new(40, 40); // screen offset of mm (0,0)
    private Vec2? _cursorMm;
    private readonly DispatcherTimer _pulseTimer;
    private readonly List<Vec2> _trail = new();
    private Vec2? _lastTrailPoint;

    public Point ToScreen(Vec2 p) => new(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);
    public Vec2 ToMm(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);
    public double Zoom => _zoom;

    public void ZoomToFit()
    {
        if (Project == null || ActualWidth < 10 || ActualHeight < 10) return;
        double w = Project.Machine.BedWidthMm, h = Project.Machine.BedHeightMm;
        double ruler = ShowRulers ? RulerPx : 0;
        double availW = ActualWidth - ruler - 48, availH = ActualHeight - ruler - 48;
        _zoom = Math.Max(0.05, Math.Min(availW / w, availH / h));
        _pan = new Vector(ruler + (ActualWidth - ruler - w * _zoom) / 2, ruler + (ActualHeight - ruler - h * _zoom) / 2);
        InvalidateVisual();
    }

    public void ZoomBy(double factor)
    {
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var before = ToMm(centre);
        _zoom = Math.Clamp(_zoom * factor, 0.05, 200);
        _pan += centre - ToScreen(before);
        InvalidateVisual();
    }

    public DesignCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        AllowDrop = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, e) => { if (e.PreviousSize.Width < 10) ZoomToFit(); };
        _pulseTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _pulseTimer.Tick += (_, _) => InvalidateVisual();
    }

    /// <summary>Adds the head's latest position to the burn trail (call while a job is running).</summary>
    public void AddTrailPoint(Vec2 p, bool burning)
    {
        if (burning && _lastTrailPoint is { } last)
        {
            if (_trail.Count == 0 || _trail[^1] != last) { _trail.Add(new Vec2(double.NaN, double.NaN)); _trail.Add(last); }
            _trail.Add(p);
            if (_trail.Count > 40_000) _trail.RemoveRange(0, 10_000);
        }
        _lastTrailPoint = p;
    }

    public void ClearTrail()
    {
        _trail.Clear();
        _lastTrailPoint = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ palette

    private static readonly Brush Backdrop = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x14)));
    private static readonly Brush BedBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0x1D, 0x21, 0x29), Color.FromRgb(0x18, 0x1B, 0x22), 90));
    private static readonly Pen BedEdge = FrozenPen(Color.FromRgb(0x39, 0x41, 0x50), 1);
    private static readonly Pen MinorGrid = FrozenPen(Color.FromArgb(0x5A, 0x2B, 0x31, 0x3D), 1);
    private static readonly Pen MajorGrid = FrozenPen(Color.FromRgb(0x2E, 0x34, 0x41), 1);
    private static readonly Color Accent = Color.FromRgb(0xFF, 0x7A, 0x1A);
    private static readonly Pen SelectionPen = FrozenPen(Accent, 1, dashed: true);
    private static readonly Pen DraftPen = FrozenPen(Accent, 1.5);
    private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Pen LaserPen = FrozenPen(Color.FromRgb(0xFF, 0x4D, 0x5E), 1.5);
    private static readonly Pen AnchorPen = FrozenPen(Color.FromRgb(0x4C, 0xB5, 0xFF), 1.5);
    private static readonly Brush AnchorFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xB5, 0xFF)));
    private static readonly Brush RulerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Pen RulerEdge = FrozenPen(Color.FromRgb(0x28, 0x2D, 0x37), 1);
    private static readonly Pen RulerTick = FrozenPen(Color.FromRgb(0x4A, 0x52, 0x62), 1);
    private static readonly Pen RulerCursor = FrozenPen(Accent, 1);
    private static readonly Brush RulerText = Frozen(new SolidColorBrush(Color.FromRgb(0x7A, 0x83, 0x96)));
    private static readonly Brush TextMuted = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB5)));
    private static readonly Brush TextDim = Frozen(new SolidColorBrush(Color.FromRgb(0x64, 0x6C, 0x7E)));
    private static readonly Brush TextBright = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF1)));
    private static readonly Brush[] BurnBrushes = Enumerable.Range(0, SimulationData.PowerBuckets)
        .Select(i => (Brush)Frozen(new SolidColorBrush(Color.FromArgb((byte)(70 + 185 * (i + 1) / SimulationData.PowerBuckets), 0xFF, 0x9A, 0x3D))))
        .ToArray();
    private static readonly Brush TravelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x4C, 0xB5, 0xFF)));
    private static readonly Brush TrailBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0x8A, 0x2A)));
    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiFaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly Dictionary<string, (Pen Pen, Brush Fill)> _layerPaint = new();
    private readonly Dictionary<Guid, (string Key, BitmapSource Bmp)> _imageCache = new();

    // ------------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Project == null) return;
        var m = Project.Machine;

        // Bed and grid
        var bed = new Rect(ToScreen(Vec2.Zero), ToScreen(new Vec2(m.BedWidthMm, m.BedHeightMm)));
        dc.DrawRoundedRectangle(BedBrush, BedEdge, bed, 3, 3);
        double step = GridStep();
        double major = step * 5;
        dc.PushClip(new RectangleGeometry(bed));
        for (double x = 0; x <= m.BedWidthMm + 1e-9; x += step)
        {
            var pen = IsMultiple(x, major) ? MajorGrid : MinorGrid;
            dc.DrawLine(pen, Snap(ToScreen(new Vec2(x, 0))), Snap(ToScreen(new Vec2(x, m.BedHeightMm))));
        }
        for (double y = 0; y <= m.BedHeightMm + 1e-9; y += step)
        {
            var pen = IsMultiple(y, major) ? MajorGrid : MinorGrid;
            dc.DrawLine(pen, Snap(ToScreen(new Vec2(0, y))), Snap(ToScreen(new Vec2(m.BedWidthMm, y))));
        }
        dc.Pop();

        bool simulating = Simulation != null;
        // Shapes, layer by layer (later layers on top). Dimmed while previewing so the burn stands out.
        if (simulating) dc.PushOpacity(0.28);
        foreach (var layer in Project.Layers.Where(l => l.Visible))
        {
            var (pen, fill) = Paint(layer.ColorHex);
            foreach (var shape in Project.ShapesOn(layer))
            {
                switch (shape)
                {
                    case ImageShape img:
                        var bmp = ImageBitmap(img, layer);
                        var b = img.GetBounds();
                        var r = new Rect(ToScreen(new Vec2(b.MinX, b.MinY)), ToScreen(new Vec2(b.MaxX, b.MaxY)));
                        dc.DrawImage(bmp, r);
                        dc.DrawRectangle(null, pen, r);
                        break;
                    case VectorShape v:
                        dc.DrawGeometry(layer.Mode == LayerMode.Fill ? fill : null, pen, BuildGeometry(v));
                        break;
                }
            }
        }
        if (simulating) dc.Pop();

        if (simulating) DrawSimulation(dc);
        else
        {
            DrawTrail(dc);
            if (Project.Shapes.Count == 0) DrawEmptyState(dc, bed);
        }

        // Selection box + resize handles
        var sel = SelectionBounds();
        if (!sel.IsEmpty && !simulating)
        {
            var r = new Rect(ToScreen(new Vec2(sel.MinX, sel.MinY)), ToScreen(new Vec2(sel.MaxX, sel.MaxY)));
            r.Inflate(3, 3);
            dc.DrawRectangle(null, SelectionPen, r);
            foreach (var h in HandlePoints(sel))
            {
                var hp = ToScreen(h);
                dc.DrawRoundedRectangle(HandleFill, DraftPen, new Rect(hp.X - HandlePx, hp.Y - HandlePx, HandlePx * 2, HandlePx * 2), 2, 2);
            }
            var size = Label($"{sel.Width:0.#} × {sel.Height:0.#} mm", 11, TextMuted);
            dc.DrawText(size, new Point(r.X + (r.Width - size.Width) / 2, r.Bottom + 6));
        }

        DrawDraft(dc);
        if (JobAnchor is { } anchor && !simulating) DrawAnchor(dc, ToScreen(anchor));
        if (LaserPosition is { } lp && !simulating) DrawHead(dc, ToScreen(lp), LaserFiring);
        if (ShowRulers) DrawRulers(dc);
    }

    private double GridStep()
    {
        // Pick a grid spacing that stays roughly 8–40 px apart on screen.
        foreach (var s in new[] { 1.0, 2, 5, 10, 20, 50, 100 })
            if (s * _zoom >= 8) return s;
        return 200;
    }

    private static bool IsMultiple(double v, double step) => Math.Abs(Math.IEEERemainder(v, step)) < 1e-6;

    private static Point Snap(Point p) => new(Math.Round(p.X) + 0.5, Math.Round(p.Y) + 0.5);

    private void DrawEmptyState(DrawingContext dc, Rect bed)
    {
        if (bed.Width < 260 || bed.Height < 160) return;
        var c = new Point(bed.X + bed.Width / 2, bed.Y + bed.Height / 2);
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x7A, 0x1A)), 1.5) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0x7A, 0x1A)), ring, new Point(c.X, c.Y - 46), 26, 26);
        // a tiny laser-head glyph
        var g = Geometry.Parse("M-8,-10 H8 V-2 H-8 Z M-3,-2 L0,4 L3,-2 M0,4 V12");
        dc.PushTransform(new TranslateTransform(c.X, c.Y - 48));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Accent), 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        dc.Pop();
        var title = Label("Start a design", 17, TextBright, bold: true);
        dc.DrawText(title, new Point(c.X - title.Width / 2, c.Y - 6));
        var line1 = Label("Draw with the tools on the left, or drop an image, SVG or project here.", 12.5, TextMuted);
        dc.DrawText(line1, new Point(c.X - line1.Width / 2, c.Y + 22));
        var line2 = Label("Ctrl+I import image  ·  T text  ·  R rectangle  ·  Tools → Material test grid", 11.5, TextDim);
        dc.DrawText(line2, new Point(c.X - line2.Width / 2, c.Y + 44));
    }

    private void DrawSimulation(DrawingContext dc)
    {
        var sim = Simulation!;
        double t = Math.Clamp(SimulationTime, 0, sim.TotalSeconds);
        dc.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, _pan.X, _pan.Y));
        double burnW = Math.Max(sim.BurnWidthMm, 1.1 / _zoom);
        var burnPens = BurnBrushes.Select(b => new Pen(b, burnW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }).ToArray();
        var travelPen = new Pen(TravelBrush, 0.8 / _zoom) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };

        int current = sim.MoveAt(t);
        foreach (var batch in sim.Batches)
        {
            if (batch.Start > current) break;
            if (batch.End - 1 < current && batch.EndSeconds <= t)
            {
                // Finished batch: cached geometry.
                sim.EnsureGeometry(batch);
                if (ShowTravel && batch.Travel != null) dc.DrawGeometry(null, travelPen, batch.Travel);
                for (int k = 0; k < SimulationData.PowerBuckets; k++)
                    if (batch.Burns![k] is { } g) dc.DrawGeometry(null, burnPens[k], g);
                continue;
            }
            // Batch in progress: move by move up to the playhead.
            for (int i = batch.Start; i < batch.End && i <= current; i++)
            {
                var mv = sim.Moves[i];
                var to = mv.To;
                if (i == current)
                {
                    double span = mv.EndSeconds - mv.StartSeconds;
                    double f = span > 1e-12 ? Math.Clamp((t - mv.StartSeconds) / span, 0, 1) : 1;
                    to = Vec2.Lerp(mv.From, mv.To, f);
                }
                if (mv.PowerFraction > 0) dc.DrawLine(burnPens[SimulationData.Bucket(mv.PowerFraction)], SimulationData.P(mv.From), SimulationData.P(to));
                else if (mv.Rapid && ShowTravel) dc.DrawLine(travelPen, SimulationData.P(mv.From), SimulationData.P(to));
            }
        }
        dc.Pop();

        if (sim.HeadAt(t) is { } head)
        {
            bool firing = current >= 0 && current < sim.Moves.Count && sim.Moves[current].PowerFraction > 0 && t < sim.TotalSeconds;
            DrawHead(dc, ToScreen(head), firing);
        }
    }

    private void DrawTrail(DrawingContext dc)
    {
        if (_trail.Count < 2) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool started = false;
            foreach (var p in _trail)
            {
                if (double.IsNaN(p.X)) { started = false; continue; }
                var s = ToScreen(p);
                if (!started) { ctx.BeginFigure(s, false, false); started = true; }
                else ctx.LineTo(s, true, false);
            }
        }
        g.Freeze();
        var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0x7A, 0x1A)), 6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, glow, g);
        dc.DrawGeometry(null, new Pen(TrailBrush, Math.Max(1.2, 0.1 * _zoom)) { LineJoin = PenLineJoin.Round }, g);
    }

    private void DrawHead(DrawingContext dc, Point c, bool firing)
    {
        if (firing)
        {
            double pulse = 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 70.0);
            double r = 12 + 5 * pulse;
            var halo = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xF2, 0xE0), 0),
                    new GradientStop(Color.FromArgb(0xE0, 0xFF, 0xA0, 0x50), 0.2),
                    new GradientStop(Color.FromArgb(0x70, 0xFF, 0x7A, 0x1A), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0x7A, 0x1A), 1),
                });
            dc.DrawEllipse(halo, null, c, r, r);
        }
        var pen = firing ? DraftPen : LaserPen;
        dc.DrawLine(pen, new Point(c.X - 14, c.Y), new Point(c.X - 5, c.Y));
        dc.DrawLine(pen, new Point(c.X + 5, c.Y), new Point(c.X + 14, c.Y));
        dc.DrawLine(pen, new Point(c.X, c.Y - 14), new Point(c.X, c.Y - 5));
        dc.DrawLine(pen, new Point(c.X, c.Y + 5), new Point(c.X, c.Y + 14));
        dc.DrawEllipse(null, pen, c, 5, 5);
    }

    private void DrawAnchor(DrawingContext dc, Point c)
    {
        dc.DrawEllipse(AnchorFill, AnchorPen, c, 7, 7);
        dc.DrawLine(AnchorPen, new Point(c.X - 12, c.Y), new Point(c.X + 12, c.Y));
        dc.DrawLine(AnchorPen, new Point(c.X, c.Y - 12), new Point(c.X, c.Y + 12));
        var label = Label("job start", 10.5, new SolidColorBrush(Color.FromRgb(0x4C, 0xB5, 0xFF)));
        dc.DrawText(label, new Point(c.X + 10, c.Y + 6));
    }

    private void DrawRulers(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, w, RulerPx));
        dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, RulerPx, h));
        dc.DrawLine(RulerEdge, new Point(0, RulerPx - 0.5), new Point(w, RulerPx - 0.5));
        dc.DrawLine(RulerEdge, new Point(RulerPx - 0.5, 0), new Point(RulerPx - 0.5, h));

        // Label spacing: at least ~60 px between numbers.
        double major = new[] { 1.0, 2, 5, 10, 20, 50, 100, 200, 500 }.FirstOrDefault(s => s * _zoom >= 60, 1000);
        double minor = major / 5;

        var tl = ToMm(new Point(RulerPx, RulerPx));
        var br = ToMm(new Point(w, h));
        dc.PushClip(new RectangleGeometry(new Rect(RulerPx, 0, Math.Max(0, w - RulerPx), RulerPx)));
        for (double x = Math.Floor(tl.X / minor) * minor; x <= br.X; x += minor)
        {
            double sx = Math.Round(ToScreen(new Vec2(x, 0)).X) + 0.5;
            bool isMajor = IsMultiple(x, major);
            dc.DrawLine(RulerTick, new Point(sx, RulerPx - (isMajor ? 9 : 4)), new Point(sx, RulerPx));
            if (isMajor)
                dc.DrawText(Label(x.ToString("0.#", CultureInfo.CurrentCulture), 10, RulerText), new Point(sx + 3, 2));
        }
        if (_cursorMm is { } cx) { double sx = ToScreen(cx).X; dc.DrawLine(RulerCursor, new Point(sx, 0), new Point(sx, RulerPx)); }
        dc.Pop();

        dc.PushClip(new RectangleGeometry(new Rect(0, RulerPx, RulerPx, Math.Max(0, h - RulerPx))));
        for (double y = Math.Floor(tl.Y / minor) * minor; y <= br.Y; y += minor)
        {
            double sy = Math.Round(ToScreen(new Vec2(0, y)).Y) + 0.5;
            bool isMajor = IsMultiple(y, major);
            dc.DrawLine(RulerTick, new Point(RulerPx - (isMajor ? 9 : 4), sy), new Point(RulerPx, sy));
            if (isMajor)
            {
                var text = Label(y.ToString("0.#", CultureInfo.CurrentCulture), 10, RulerText);
                dc.PushTransform(new RotateTransform(-90, 2, sy - 3));
                dc.DrawText(text, new Point(2 - text.Width, sy - 3));
                dc.Pop();
            }
        }
        if (_cursorMm is { } cy) { double sy = ToScreen(cy).Y; dc.DrawLine(RulerCursor, new Point(0, sy), new Point(RulerPx, sy)); }
        dc.Pop();

        dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, RulerPx, RulerPx));
        var mmLabel = Label("mm", 9, TextDim);
        dc.DrawText(mmLabel, new Point((RulerPx - mmLabel.Width) / 2, (RulerPx - mmLabel.Height) / 2));
    }

    private FormattedText Label(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiFaceBold : UiFace, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private StreamGeometry BuildGeometry(VectorShape v)
    {
        var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var ctx = g.Open())
        {
            foreach (var path in v.Paths)
            {
                if (path.Points.Count < 2) continue;
                ctx.BeginFigure(ToScreen(path.Points[0]), path.Closed, path.Closed);
                ctx.PolyLineTo(path.Points.Skip(1).Select(ToScreen).ToList(), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    private BitmapSource ImageBitmap(ImageShape img, Layer layer)
    {
        string key = string.Join("|", RuntimeHelpers.GetHashCode(img.Gray), img.PixelWidth, img.Brightness, img.Contrast,
            img.Gamma, img.Invert, img.Threshold, img.Dither, PreviewRaster,
            PreviewRaster ? $"{layer.LineIntervalMm}|{img.WidthMm:0.##}|{img.HeightMm:0.##}" : "");
        if (_imageCache.TryGetValue(img.Id, out var cached) && cached.Key == key) return cached.Bmp;

        BitmapSource bmp;
        if (PreviewRaster)
        {
            // Show exactly the dots that will be burned.
            var (px, w, h) = ImageProcessor.Prepare(img, layer.LineIntervalMm);
            bmp = ImageLoader.ToBitmap(px, w, h);
        }
        else
        {
            bmp = ImageLoader.ToBitmap(ImageProcessor.Adjust(img.Gray, img.Brightness, img.Contrast, img.Gamma, img.Invert),
                                       img.PixelWidth, img.PixelHeight);
        }
        _imageCache[img.Id] = (key, bmp);
        return bmp;
    }

    private (Pen, Brush) Paint(string hex)
    {
        if (_layerPaint.TryGetValue(hex, out var p)) return p;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = Colors.White; }
        var pen = FrozenPen(c, 1.4);
        var fill = Frozen(new SolidColorBrush(Color.FromArgb(0x38, c.R, c.G, c.B)));
        _layerPaint[hex] = (pen, fill);
        return (pen, fill);
    }

    // ------------------------------------------------------------------ selection helpers

    public IEnumerable<CoreShape> SelectedShapes() =>
        Project?.Shapes.Where(s => Selection.Contains(s.Id)) ?? Enumerable.Empty<CoreShape>();

    public Bounds SelectionBounds() => SelectedShapes().Aggregate(Bounds.Empty, (b, s) => b.Union(s.GetBounds()));

    public void SelectOnly(IEnumerable<Guid> ids)
    {
        Selection.Clear();
        foreach (var id in ids) Selection.Add(id);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    private const double HandlePx = 4.5;

    private static Vec2[] HandlePoints(Bounds b) => new[]
    {
        new Vec2(b.MinX, b.MinY), new Vec2(b.MaxX, b.MinY), new Vec2(b.MaxX, b.MaxY), new Vec2(b.MinX, b.MaxY),
    };

    private CoreShape? HitTest(Vec2 p)
    {
        if (Project == null) return null;
        double tol = 5 / _zoom;
        var visible = Project.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
        for (int i = Project.Shapes.Count - 1; i >= 0; i--)
        {
            var s = Project.Shapes[i];
            if (visible.Contains(s.LayerId) && s.HitTest(p, tol)) return s;
        }
        return null;
    }

    // ------------------------------------------------------------------ drag and drop

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            FilesDropped?.Invoke(files, ToMm(e.GetPosition(this)));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ mouse interaction

    private enum Drag { None, Pan, Move, Resize, Rubber, Draw }
    private Drag _drag;
    private Point _lastScreen;
    private Vec2 _dragStartMm, _dragNowMm, _anchor, _moveOrigin;
    private bool _changeAnnounced;
    private readonly List<Vec2> _penPoints = new();

    private Vec2 Snap(Vec2 p) => SnapToGrid && !Keyboard.IsKeyDown(Key.LeftAlt)
        ? new Vec2(Math.Round(p.X / SnapMm) * SnapMm, Math.Round(p.Y / SnapMm) * SnapMm)
        : p;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        var before = ToMm(pos);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.05, 200);
        var after = ToScreen(before);
        _pan += pos - after;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _cursorMm = null;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (Project == null) return;
        var pos = e.GetPosition(this);
        var mm = ToMm(pos);
        _lastScreen = pos;

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            _drag = Drag.Pan;
            Cursor = Cursors.SizeAll;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || Simulation != null) return;

        switch (Tool)
        {
            case DesignTool.Select:
                BeginSelectDrag(mm, pos);
                break;
            case DesignTool.Pen:
                HandlePenClick(Snap(mm), e.ClickCount);
                break;
            case DesignTool.Text:
                var shape = TextRequested?.Invoke(Snap(mm));
                if (shape != null) AddShape(shape);
                break;
            default:
                _drag = Drag.Draw;
                _dragStartMm = _dragNowMm = Snap(mm);
                CaptureMouse();
                break;
        }
        InvalidateVisual();
    }

    private void BeginSelectDrag(Vec2 mm, Point screen)
    {
        // Resize handle?
        var sel = SelectionBounds();
        if (!sel.IsEmpty)
        {
            var handles = HandlePoints(sel);
            for (int i = 0; i < handles.Length; i++)
            {
                var hp = ToScreen(handles[i]);
                if (Math.Abs(hp.X - screen.X) <= HandlePx + 3 && Math.Abs(hp.Y - screen.Y) <= HandlePx + 3)
                {
                    _anchor = handles[(i + 2) % 4]; // opposite corner stays put
                    _drag = Drag.Resize;
                    _changeAnnounced = false;
                    CaptureMouse();
                    return;
                }
            }
        }

        var hit = HitTest(mm);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (hit != null)
        {
            if (ctrl)
            {
                if (!Selection.Remove(hit.Id)) Selection.Add(hit.Id);
            }
            else if (!Selection.Contains(hit.Id))
            {
                Selection.Clear();
                Selection.Add(hit.Id);
            }
            SelectionChanged?.Invoke();
            _drag = Drag.Move;
            _dragStartMm = _dragNowMm = mm;
            var sb = SelectionBounds();
            _moveOrigin = new Vec2(sb.MinX, sb.MinY);
            _changeAnnounced = false;
        }
        else
        {
            if (!ctrl) { Selection.Clear(); SelectionChanged?.Invoke(); }
            _drag = Drag.Rubber;
            _dragStartMm = _dragNowMm = mm;
        }
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        var mm = ToMm(pos);
        _cursorMm = mm;
        CursorMoved?.Invoke(mm);

        switch (_drag)
        {
            case Drag.Pan:
                _pan += pos - _lastScreen;
                break;
            case Drag.Move:
            {
                // Position the selection's top-left corner (snapped) rather than nudging by cursor deltas,
                // so shapes land on grid lines and there is no accumulated drift.
                var desired = Snap(_moveOrigin + (mm - _dragStartMm));
                var b = SelectionBounds();
                var delta = desired - new Vec2(b.MinX, b.MinY);
                if (Math.Abs(delta.X) > 1e-9 || Math.Abs(delta.Y) > 1e-9)
                {
                    Announce();
                    var t = Transform2D.Translate(delta.X, delta.Y);
                    foreach (var s in SelectedShapes()) s.ApplyTransform(t);
                }
                break;
            }
            case Drag.Resize:
                ResizeTo(Snap(mm), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || AllImages());
                break;
            case Drag.Rubber:
            case Drag.Draw:
                _dragNowMm = _drag == Drag.Draw ? Snap(mm) : mm;
                break;
            default:
                if (Tool == DesignTool.Pen && _penPoints.Count > 0) _dragNowMm = Snap(mm);
                break;
        }
        _lastScreen = pos;
        InvalidateVisual();
    }

    private bool AllImages() => SelectedShapes().All(s => s is ImageShape);

    private void ResizeTo(Vec2 corner, bool keepAspect)
    {
        var b = SelectionBounds();
        if (b.IsEmpty || b.Width < 1e-6 || b.Height < 1e-6) return;
        double w = Math.Max(0.1, Math.Abs(corner.X - _anchor.X));
        double h = Math.Max(0.1, Math.Abs(corner.Y - _anchor.Y));
        if (keepAspect)
        {
            double s = Math.Max(w / b.Width, h / b.Height);
            w = b.Width * s; h = b.Height * s;
        }
        double minX = corner.X < _anchor.X ? _anchor.X - w : _anchor.X;
        double minY = corner.Y < _anchor.Y ? _anchor.Y - h : _anchor.Y;
        Announce();
        var t = Transform2D.Translate(-b.MinX, -b.MinY)
            .Then(Transform2D.Scale(w / b.Width, h / b.Height))
            .Then(Transform2D.Translate(minX, minY));
        foreach (var s in SelectedShapes()) s.ApplyTransform(t);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        if (drag == Drag.Pan) Cursor = Tool == DesignTool.Select ? Cursors.Arrow : Cursors.Cross;

        switch (drag)
        {
            case Drag.Move:
            case Drag.Resize:
                if (_changeAnnounced) Changed?.Invoke();
                break;
            case Drag.Rubber:
                var box = Bounds.Of(new[] { _dragStartMm, _dragNowMm });
                if (Project != null && box.Width * _zoom > 3)
                {
                    var visible = Project.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
                    foreach (var s in Project.Shapes.Where(s => visible.Contains(s.LayerId) && box.Intersects(s.GetBounds())))
                        Selection.Add(s.Id);
                    SelectionChanged?.Invoke();
                }
                break;
            case Drag.Draw:
                FinishDraw();
                break;
        }
        InvalidateVisual();
    }

    private void FinishDraw()
    {
        var a = _dragStartMm; var b = _dragNowMm;
        double w = b.X - a.X, h = b.Y - a.Y;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && Tool != DesignTool.Line)
        {
            double s = Math.Max(Math.Abs(w), Math.Abs(h));
            w = Math.Sign(w == 0 ? 1 : w) * s; h = Math.Sign(h == 0 ? 1 : h) * s;
        }
        if (Math.Abs(w) < 0.2 && Math.Abs(h) < 0.2) return;
        CoreShape shape = Tool switch
        {
            DesignTool.Rectangle => ShapeFactory.Rectangle(a.X, a.Y, w, h),
            DesignTool.Ellipse => ShapeFactory.Ellipse(a.X, a.Y, w, h),
            DesignTool.Polygon => ShapeFactory.Polygon(a.X, a.Y, w, h, PolygonSides),
            _ => ShapeFactory.Line(a, b),
        };
        AddShape(shape);
    }

    private void HandlePenClick(Vec2 p, int clickCount)
    {
        if (clickCount >= 2)
        {
            FinishPen(closed: false);
            return;
        }
        if (_penPoints.Count >= 3 && Vec2.Distance(p, _penPoints[0]) * _zoom < 8)
        {
            FinishPen(closed: true);
            return;
        }
        _penPoints.Add(p);
        _dragNowMm = p;
    }

    public void FinishPen(bool closed)
    {
        var pts = _penPoints.Distinct().ToList();
        _penPoints.Clear();
        if (pts.Count >= 2) AddShape(ShapeFactory.Path(pts, closed && pts.Count >= 3));
        InvalidateVisual();
    }

    public void CancelDraft()
    {
        _penPoints.Clear();
        _drag = Drag.None;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    public void AddShape(CoreShape shape) => AddShapes(new[] { shape });

    /// <summary>Adds shapes to the active layer (steering images and vectors to a suitable layer) and selects them.</summary>
    public void AddShapes(IReadOnlyList<CoreShape> shapes)
    {
        if (Project == null || shapes.Count == 0) return;
        BeforeChange?.Invoke();
        foreach (var shape in shapes)
        {
            var layer = Project.FindLayer(ActiveLayerId) ?? Project.Layers.FirstOrDefault();
            if (layer == null) { layer = LaserProject.DefaultLayer(LayerMode.Line); Project.Layers.Add(layer); }
            // Images need an image layer; vectors on an image layer would be filled, so steer them to the active vector layer.
            if (shape is ImageShape && layer.Mode != LayerMode.Image) layer = Project.GetOrCreateLayer(LayerMode.Image);
            if (shape is VectorShape && layer.Mode == LayerMode.Image)
                layer = Project.Layers.FirstOrDefault(l => l.Mode != LayerMode.Image) ?? Project.GetOrCreateLayer(LayerMode.Line);
            if (shape.LayerId == Guid.Empty || Project.FindLayer(shape.LayerId) == null) shape.LayerId = layer.Id;
            Project.Shapes.Add(shape);
        }
        SelectOnly(shapes.Select(s => s.Id));
        Changed?.Invoke();
    }

    private void Announce()
    {
        if (_changeAnnounced) return;
        _changeAnnounced = true;
        BeforeChange?.Invoke();
    }

    private void DrawDraft(DrawingContext dc)
    {
        if (_drag == Drag.Rubber)
        {
            dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0x7A, 0x1A))), SelectionPen,
                             new Rect(ToScreen(_dragStartMm), ToScreen(_dragNowMm)));
        }
        else if (_drag == Drag.Draw)
        {
            var r = new Rect(ToScreen(_dragStartMm), ToScreen(_dragNowMm));
            switch (Tool)
            {
                case DesignTool.Rectangle: dc.DrawRectangle(null, DraftPen, r); break;
                case DesignTool.Ellipse:
                case DesignTool.Polygon:
                    dc.DrawEllipse(null, DraftPen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                    break;
                case DesignTool.Line: dc.DrawLine(DraftPen, ToScreen(_dragStartMm), ToScreen(_dragNowMm)); break;
            }
            var size = _dragNowMm - _dragStartMm;
            var label = Label($"{Math.Abs(size.X):0.#} × {Math.Abs(size.Y):0.#} mm", 11, TextMuted);
            dc.DrawText(label, ToScreen(_dragNowMm) + new Vector(10, 10));
        }
        if (Tool == DesignTool.Pen && _penPoints.Count > 0)
        {
            var pts = _penPoints.Select(ToScreen).Append(ToScreen(_dragNowMm)).ToList();
            for (int i = 1; i < pts.Count; i++) dc.DrawLine(DraftPen, pts[i - 1], pts[i]);
            dc.DrawEllipse(HandleFill, DraftPen, pts[0], 4, 4);
        }
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Pen FrozenPen(Color c, double thickness, bool dashed = false)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        if (dashed) p.DashStyle = DashStyles.Dash;
        p.Freeze();
        return p;
    }

    public void InvalidateImages() => _imageCache.Clear();
}
