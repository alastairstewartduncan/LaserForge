using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;
using CoreShape = AsdLasercraft.Core.Model.Shape;
using Transform2D = AsdLasercraft.Core.Geometry.Transform2D;
using Vec2 = AsdLasercraft.Core.Geometry.Vec2;

namespace AsdLasercraft.App;

// Edit and Arrange: undo, selection, transforms, the selection panel, text, arrays, image settings and tracing.
public partial class MainWindow
{
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var p = _undo.Undo(_project);
        if (p != null) { SetProject(p, _filePath, fit: false); MarkDirty(); }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        var p = _undo.Redo(_project);
        if (p != null) { SetProject(p, _filePath, fit: false); MarkDirty(); }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var copies = new List<CoreShape>();
        Edit(() =>
        {
            foreach (var s in sel)
            {
                var c = s.Clone();
                c.ApplyTransform(Transform2D.Translate(5, 5));
                _project.Shapes.Add(c);
                copies.Add(c);
            }
        });
        Surface.SelectOnly(copies.Select(c => c.Id));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var ids = Surface.Selection.ToHashSet();
        if (ids.Count == 0) return;
        Edit(() => _project.Shapes.RemoveAll(s => ids.Contains(s.Id)));
        Surface.SelectOnly(Array.Empty<Guid>());
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        Surface.SelectOnly(_project.Shapes.Select(s => s.Id));

    private void RotateCw_Click(object sender, RoutedEventArgs e) => RotateSelection(90);
    private void RotateCcw_Click(object sender, RoutedEventArgs e) => RotateSelection(-90);

    /// <summary>Rotates the selection about its centre. Project space is Y-down, so positive = clockwise on screen.</summary>
    private void RotateSelection(double degrees)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        bool quarterTurn = Math.Abs(Math.IEEERemainder(degrees, 90)) < 1e-9;
        if (!quarterTurn && sel.Any(s => s is ImageShape))
        {
            ShowToast("Images can only be rotated in 90° steps.", ToastKind.Warning);
            return;
        }
        var centre = Surface.SelectionBounds().Center;
        var rot = Transform2D.Rotate(degrees, centre);
        Edit(() =>
        {
            foreach (var s in sel)
            {
                if (s is ImageShape img)
                {
                    var newCentre = rot.Apply(img.GetBounds().Center);
                    int turns = ((int)Math.Round(degrees / 90) % 4 + 4) % 4;
                    for (int i = 0; i < turns; i++) img.Rotate90(clockwise: true);
                    img.MoveTo(newCentre.X - img.WidthMm / 2, newCentre.Y - img.HeightMm / 2);
                }
                else s.ApplyTransform(rot);
            }
        });
    }

    private void MirrorH_Click(object sender, RoutedEventArgs e) => MirrorSelection(horizontal: true);
    private void MirrorV_Click(object sender, RoutedEventArgs e) => MirrorSelection(horizontal: false);

    private void MirrorSelection(bool horizontal)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var c = Surface.SelectionBounds().Center;
        var t = Transform2D.Scale(horizontal ? -1 : 1, horizontal ? 1 : -1, c);
        Edit(() =>
        {
            foreach (var s in sel)
            {
                s.ApplyTransform(t); // vectors mirror; images get their box mirrored about the group centre…
                if (s is ImageShape img) FlipPixels(img, horizontal); // …and their pixels flipped here
            }
        });
    }

    private static void FlipPixels(ImageShape img, bool horizontal)
    {
        int w = img.PixelWidth, h = img.PixelHeight;
        var dst = new byte[img.Gray.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dst[y * w + x] = horizontal ? img.Gray[y * w + (w - 1 - x)] : img.Gray[(h - 1 - y) * w + x];
        img.Gray = dst;
    }

    private void CentreOnBed_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var c = Surface.SelectionBounds().Center;
        var t = Transform2D.Translate(_project.Machine.BedWidthMm / 2 - c.X, _project.Machine.BedHeightMm / 2 - c.Y);
        Edit(() => sel.ForEach(s => s.ApplyTransform(t)));
    }

    private void MoveToOrigin_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var b = Surface.SelectionBounds();
        var t = Transform2D.Translate(-b.MinX, -b.MinY);
        Edit(() => sel.ForEach(s => s.ApplyTransform(t)));
    }

    private void Nudge(double dx, double dy)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        Edit(() => sel.ForEach(s => s.ApplyTransform(Transform2D.Translate(dx, dy))));
    }

    // ================================================================== selection panel

    private void RefreshSelectionUi()
    {
        _updatingUi = true;
        try
        {
            var sel = Selected();
            var b = Surface.SelectionBounds();
            bool any = sel.Count > 0;
            SelectionGroup.IsEnabled = any;
            XBox.Text = any ? Num(b.MinX) : "";
            YBox.Text = any ? Num(b.MinY) : "";
            WBox.Text = any ? Num(b.Width) : "";
            HBox.Text = any ? Num(b.Height) : "";
            SelectionText.Text = any
                ? $"{sel.Count} selected  ·  {b.Width:0.#} × {b.Height:0.#} mm"
                : "Nothing selected";

            if (sel.Count == 1 && sel[0] is ImageShape img)
            {
                ImageGroup.Visibility = Visibility.Visible;
                DitherCombo.SelectedItem = img.Dither;
                BrightnessSlider.Value = img.Brightness;
                ContrastSlider.Value = img.Contrast;
                GammaSlider.Value = img.Gamma;
                ThresholdSlider.Value = img.Threshold;
                InvertCheck.IsChecked = img.Invert;
            }
            else ImageGroup.Visibility = Visibility.Collapsed;
        }
        finally { _updatingUi = false; }
        UpdateAnchorMarker();
    }

    private void Geometry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Geometry_Commit(sender, e); e.Handled = true; }
    }

    private void Geometry_Commit(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        var sel = Selected();
        if (sel.Count == 0) return;
        var b = Surface.SelectionBounds();
        if (!TryNum(XBox.Text, out var x) || !TryNum(YBox.Text, out var y) ||
            !TryNum(WBox.Text, out var w) || !TryNum(HBox.Text, out var h))
        {
            RefreshSelectionUi();
            return;
        }
        w = Math.Max(0, w); h = Math.Max(0, h);
        bool wChanged = Math.Abs(w - b.Width) > 0.005, hChanged = Math.Abs(h - b.Height) > 0.005;
        if (LockAspect.IsChecked == true && b.Width > 1e-9 && b.Height > 1e-9)
        {
            if (wChanged) h = w * b.Height / b.Width;
            else if (hChanged) w = h * b.Width / b.Height;
        }
        bool moved = Math.Abs(x - b.MinX) > 0.005 || Math.Abs(y - b.MinY) > 0.005;
        if (!moved && !wChanged && !hChanged) return;

        double sx = b.Width > 1e-9 ? w / b.Width : 1, sy = b.Height > 1e-9 ? h / b.Height : 1;
        var t = Transform2D.Translate(-b.MinX, -b.MinY).Then(Transform2D.Scale(sx, sy)).Then(Transform2D.Translate(x, y));
        Edit(() => sel.ForEach(s => s.ApplyTransform(t)));
    }

    private void Rotate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (TryNum(RotateBox.Text, out var deg) && Math.Abs(deg) > 1e-9) RotateSelection(deg);
        RotateBox.Text = "0";
        e.Handled = true;
    }

    private void ImageSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || Surface == null) return;
        if (Selected() is not [ImageShape img]) return;
        if (DitherCombo.SelectedItem is DitherMode mode) img.Dither = mode;
        img.Brightness = Math.Round(BrightnessSlider.Value);
        img.Contrast = Math.Round(ContrastSlider.Value);
        img.Gamma = Math.Round(GammaSlider.Value, 2);
        img.Threshold = (byte)Math.Round(ThresholdSlider.Value);
        img.Invert = InvertCheck.IsChecked == true;
        MarkDirty();
        Surface.InvalidateVisual();
        UpdateJobEstimate();
    }

    private CoreShape? OnTextRequested(Vec2 at)
    {
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s).ToList();
        var defaultFont = fonts.FirstOrDefault(f => f == "Arial") ?? fonts.FirstOrDefault() ?? "Segoe UI";
        var form = new FormDialog("Add text", this)
            .Text("Text", "Hello")
            .Choice("Font", fonts, defaultFont)
            .Number("Size (mm)", 10)
            .Check("Bold", false)
            .Check("Italic", false);
        if (!form.Show(out var r)) return null;
        var text = r.GetText("Text");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var paths = TextToPath.Build(text, new FontFamily(r.GetChoice<string>("Font")), Math.Max(0.5, r.GetNumber("Size (mm)", 10)),
                                     r.GetCheck("Bold"), r.GetCheck("Italic"), at);
        return paths.Count == 0 ? null : new VectorShape(paths, $"Text \"{text}\"");
    }

    // ================================================================== arrays

    private void ArrayGrid_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) { ShowToast("Select something to copy first.", ToastKind.Info); return; }
        var form = new FormDialog("Grid array", this)
            .Hint("Copies the selection into rows and columns. Spacing is the selection's size plus the gap.")
            .Number("Columns", 3).Number("Rows", 2)
            .Number("Gap X (mm)", 5).Number("Gap Y (mm)", 5);
        if (!form.Show(out var r)) return;
        int cols = Math.Clamp(r.GetInt("Columns", 3), 1, 100), rows = Math.Clamp(r.GetInt("Rows", 2), 1, 100);
        var copies = ArrayTool.Grid(sel, cols, rows, r.GetNumber("Gap X (mm)", 5), r.GetNumber("Gap Y (mm)", 5));
        AddArrayCopies(sel, copies);
    }

    private void ArrayCircular_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) { ShowToast("Select something to copy first.", ToastKind.Info); return; }
        var form = new FormDialog("Circular array", this)
            .Hint("Copies the selection round a circle. The selection stays at 12 o'clock and the circle's centre is below it.")
            .Number("Total items", 8)
            .Number("Radius (mm)", 40)
            .Check("Rotate copies to face outwards", true);
        if (!form.Show(out var r)) return;
        int count = Math.Clamp(r.GetInt("Total items", 8), 2, 360);
        double radius = Math.Max(1, r.GetNumber("Radius (mm)", 40));
        var c = Surface.SelectionBounds().Center;
        var copies = ArrayTool.Circular(sel, count, new Vec2(c.X, c.Y + radius), r.GetCheck("Rotate copies to face outwards"));
        AddArrayCopies(sel, copies);
    }

    private void AddArrayCopies(List<CoreShape> originals, List<CoreShape> copies)
    {
        if (copies.Count == 0) return;
        Edit(() => _project.Shapes.AddRange(copies));
        Surface.SelectOnly(originals.Concat(copies).Select(s => s.Id));
        ShowToast($"Added {copies.Count} cop{(copies.Count == 1 ? "y" : "ies")}");
    }

    // ================================================================== trace image

    private async void TraceImage_Click(object sender, RoutedEventArgs e)
    {
        var img = Selected().OfType<ImageShape>().FirstOrDefault()
                  ?? (_project.Shapes.OfType<ImageShape>().Count() == 1 ? _project.Shapes.OfType<ImageShape>().First() : null);
        if (img == null) { ShowToast("Select an image to trace.", ToastKind.Info); return; }

        var form = new FormDialog("Trace image to vectors", this)
            .Hint("Turns the dark parts of the image into outlines you can cut, score or fill. The image's brightness, contrast and invert settings are used.")
            .Number("Threshold (1–254)", img.Threshold, "Pixels darker than this are traced.")
            .Number("Smoothing (mm, 0 = auto)", 0, "How far the outline may stray from the pixel edges. Bigger = smoother, fewer points.")
            .Number("Ignore specks under (mm²)", 0.25)
            .Check("Keep the original image", true);
        if (!form.Show(out var r)) return;
        var options = new TraceOptions
        {
            Threshold = (byte)Math.Clamp(r.GetInt("Threshold (1–254)", img.Threshold), 1, 254),
            SmoothingMm = Math.Max(0, r.GetNumber("Smoothing (mm, 0 = auto)", 0)),
            MinAreaMm2 = Math.Max(0, r.GetNumber("Ignore specks under (mm²)", 0.25)),
        };
        bool keep = r.GetCheck("Keep the original image");

        Cursor = Cursors.Wait;
        List<AsdLasercraft.Core.Geometry.Polyline> paths;
        try { paths = await Task.Run(() => ImageTracer.Trace(img, options)); }
        finally { Cursor = null; }
        if (paths.Count == 0) { ShowToast("Nothing dark enough to trace – try a higher threshold.", ToastKind.Warning); return; }

        var layer = ActiveLayer is { Mode: not LayerMode.Image } active ? active
            : _project.Layers.FirstOrDefault(l => l.Mode == LayerMode.Fill) ?? _project.GetOrCreateLayer(LayerMode.Fill);
        var shape = new VectorShape(paths, $"Traced {img.Name}") { LayerId = layer.Id };
        Edit(() =>
        {
            _project.Shapes.Add(shape);
            if (!keep) _project.Shapes.Remove(img);
        });
        Surface.SelectOnly(new[] { shape.Id });
        ShowToast($"Traced {paths.Count} outline{(paths.Count == 1 ? "" : "s")} onto layer '{layer.Name}'");
    }
}
