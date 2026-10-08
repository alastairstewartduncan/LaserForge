using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// The work area: draws the bed, grid, shapes and images in millimetre space with zoom/pan,
/// and handles selection, moving, resizing and drawing new shapes.
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

    /// <summary>Laser head position in project space, shown as a crosshair.</summary>
    public Vec2? LaserPosition { get; set; }

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

    // ------------------------------------------------------------------ view transform

    private double _zoom = 2;          // screen px per mm
    private Vector _pan = new(20, 20); // screen offset of mm (0,0)

    public Point ToScreen(Vec2 p) => new(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);
    public Vec2 ToMm(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    public void ZoomToFit()
    {
        if (Project == null || ActualWidth < 10 || ActualHeight < 10) return;
        double w = Project.Machine.BedWidthMm, h = Project.Machine.BedHeightMm;
        _zoom = Math.Min((ActualWidth - 40) / w, (ActualHeight - 40) / h);
        _pan = new Vector((ActualWidth - w * _zoom) / 2, (ActualHeight - h * _zoom) / 2);
        InvalidateVisual();
    }

    public DesignCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, e) => { if (e.PreviousSize.Width < 10) ZoomToFit(); };
    }

    // ------------------------------------------------------------------ rendering

    private static readonly Brush Backdrop = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)));
    private static readonly Brush BedBrush = Brushes.White;
    private static readonly Pen MinorGrid = FrozenPen(Color.FromRgb(0xEE, 0xEE, 0xEE), 1);
    private static readonly Pen MajorGrid = FrozenPen(Color.FromRgb(0xD0, 0xD4, 0xDA), 1);
    private static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0x00, 0x78, 0xD4), 1, dashed: true);
    private static readonly Pen DraftPen = FrozenPen(Color.FromRgb(0x00, 0x78, 0xD4), 1.5);
    private static readonly Pen LaserPen = FrozenPen(Color.FromRgb(0xFF, 0x00, 0x60), 1.5);
    private readonly Dictionary<string, (Pen Pen, Brush Fill)> _layerPaint = new();
    private readonly Dictionary<Guid, (string Key, BitmapSource Bmp)> _imageCache = new();

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Project == null) return;
        var m = Project.Machine;

        // Bed and grid
        var bed = new Rect(ToScreen(Vec2.Zero), ToScreen(new Vec2(m.BedWidthMm, m.BedHeightMm)));
        dc.DrawRectangle(BedBrush, null, bed);
        double step = _zoom >= 4 ? 5 : 10;
        for (double x = 0; x <= m.BedWidthMm + 1e-9; x += step)
        {
            var pen = Math.Abs(x % 50) < 1e-9 ? MajorGrid : MinorGrid;
            dc.DrawLine(pen, ToScreen(new Vec2(x, 0)), ToScreen(new Vec2(x, m.BedHeightMm)));
        }
        for (double y = 0; y <= m.BedHeightMm + 1e-9; y += step)
        {
            var pen = Math.Abs(y % 50) < 1e-9 ? MajorGrid : MinorGrid;
            dc.DrawLine(pen, ToScreen(new Vec2(0, y)), ToScreen(new Vec2(m.BedWidthMm, y)));
        }

        // Shapes, layer by layer (later layers on top)
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
                        dc.DrawImage(bmp, new Rect(ToScreen(new Vec2(b.MinX, b.MinY)), ToScreen(new Vec2(b.MaxX, b.MaxY))));
                        dc.DrawRectangle(null, pen, new Rect(ToScreen(new Vec2(b.MinX, b.MinY)), ToScreen(new Vec2(b.MaxX, b.MaxY))));
                        break;
                    case VectorShape v:
                        dc.DrawGeometry(layer.Mode == LayerMode.Fill ? fill : null, pen, BuildGeometry(v));
                        break;
                }
            }
        }

        // Selection box + resize handles
        var sel = SelectionBounds();
        if (!sel.IsEmpty)
        {
            var r = new Rect(ToScreen(new Vec2(sel.MinX, sel.MinY)), ToScreen(new Vec2(sel.MaxX, sel.MaxY)));
            r.Inflate(3, 3);
            dc.DrawRectangle(null, SelectionPen, r);
            foreach (var h in HandlePoints(sel))
                dc.DrawRectangle(Brushes.White, DraftPen, new Rect(ToScreen(h).X - HandlePx, ToScreen(h).Y - HandlePx, HandlePx * 2, HandlePx * 2));
        }

        DrawDraft(dc);

        if (LaserPosition is { } lp)
        {
            var c = ToScreen(lp);
            dc.DrawLine(LaserPen, new Point(c.X - 10, c.Y), new Point(c.X + 10, c.Y));
            dc.DrawLine(LaserPen, new Point(c.X, c.Y - 10), new Point(c.X, c.Y + 10));
            dc.DrawEllipse(null, LaserPen, c, 4, 4);
        }
    }

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
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = Colors.Black; }
        var pen = FrozenPen(c, 1.3);
        var fill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)));
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

    private const double HandlePx = 4;

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
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.2, 200);
        var after = ToScreen(before);
        _pan += pos - after;
        InvalidateVisual();
        e.Handled = true;
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
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

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
                if (Math.Abs(hp.X - screen.X) <= HandlePx + 2 && Math.Abs(hp.Y - screen.Y) <= HandlePx + 2)
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
                else return;
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

    public void AddShape(CoreShape shape)
    {
        if (Project == null) return;
        BeforeChange?.Invoke();
        var layer = Project.FindLayer(ActiveLayerId) ?? Project.Layers.FirstOrDefault();
        if (layer == null) { layer = LaserProject.DefaultLayer(LayerMode.Line); Project.Layers.Add(layer); }
        // Images need an image layer; vectors on an image layer would be filled, so steer them to the active vector layer.
        if (shape is ImageShape && layer.Mode != LayerMode.Image) layer = Project.GetOrCreateLayer(LayerMode.Image);
        if (shape is VectorShape && layer.Mode == LayerMode.Image)
            layer = Project.Layers.FirstOrDefault(l => l.Mode != LayerMode.Image) ?? Project.GetOrCreateLayer(LayerMode.Line);
        shape.LayerId = layer.Id;
        Project.Shapes.Add(shape);
        SelectOnly(new[] { shape.Id });
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
            dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD4))), SelectionPen,
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
            var label = new FormattedText($"{Math.Abs(size.X):0.#} × {Math.Abs(size.Y):0.#} mm", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, ToScreen(_dragNowMm) + new Vector(8, 8));
        }
        if (Tool == DesignTool.Pen && _penPoints.Count > 0)
        {
            var pts = _penPoints.Select(ToScreen).Append(ToScreen(_dragNowMm)).ToList();
            for (int i = 1; i < pts.Count; i++) dc.DrawLine(DraftPen, pts[i - 1], pts[i]);
            dc.DrawEllipse(Brushes.White, DraftPen, pts[0], 4, 4);
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
