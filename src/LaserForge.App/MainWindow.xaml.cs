using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LaserForge.App.Controls;
using LaserForge.App.Dialogs;
using LaserForge.App.Services;
using LaserForge.Core;
using LaserForge.Core.Gcode;
using LaserForge.Core.Generators;
using LaserForge.Core.Grbl;
using LaserForge.Core.Imaging;
using LaserForge.Core.Import;
using LaserForge.Core.Model;
using LaserForge.Core.Serialization;
using Microsoft.Win32;
using Bounds = LaserForge.Core.Geometry.Bounds;
using CoreShape = LaserForge.Core.Model.Shape;
using Transform2D = LaserForge.Core.Geometry.Transform2D;
using Vec2 = LaserForge.Core.Geometry.Vec2;

namespace LaserForge.App;

public partial class MainWindow : Window
{
    private const string ProjectFilter = "LaserForge project (*.lfp)|*.lfp";
    private const string GcodeFilter = "G-code (*.gcode;*.nc)|*.gcode;*.nc|All files|*.*";
    private static readonly string[] Palette =
    {
        "#1E5AFF", "#E5341C", "#2EA043", "#B04CE0", "#F08C00", "#00A3A3",
        "#D6338A", "#6B7B00", "#5A5A5A", "#0F3D91", "#8C2D19", "#000000",
    };

    private LaserProject _project = LaserProject.CreateDefault();
    private readonly UndoStack _undo = new();
    private string? _filePath;
    private bool _dirty;
    private bool _updatingUi;
    private int _estimateVersion;

    private SerialTransport? _transport;
    private GrblStreamer? _streamer;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow()
    {
        _updatingUi = true;
        InitializeComponent();
        _updatingUi = false;

        ModeColumn.ItemsSource = Enum.GetValues<LayerMode>();
        DitherCombo.ItemsSource = Enum.GetValues<DitherMode>();

        Surface.BeforeChange += () => { _undo.Push(_project); MarkDirty(); };
        Surface.Changed += () => { RefreshSelectionUi(); UpdateJobEstimate(); };
        Surface.SelectionChanged += RefreshSelectionUi;
        Surface.CursorMoved += p => CursorText.Text = $"X {p.X:0.0}  Y {p.Y:0.0} mm";
        Surface.TextRequested += OnTextRequested;
        _statusTimer.Tick += (_, _) => _streamer?.RequestStatus();

        SetProject(LaserProject.CreateDefault(), null, fit: true);
        RefreshPorts();
    }

    // ================================================================== project state

    private Layer? ActiveLayer => LayersGrid.SelectedItem as Layer;

    private void SetProject(LaserProject project, string? path, bool fit)
    {
        int layerIndex = Math.Max(0, LayersGrid.SelectedIndex);
        _project = project;
        _filePath = path;
        Surface.Project = project;
        Surface.Selection.Clear();
        Surface.InvalidateImages();
        LayersGrid.ItemsSource = null;
        LayersGrid.ItemsSource = project.Layers;
        LayersGrid.SelectedIndex = fit ? 0 : Math.Min(layerIndex, project.Layers.Count - 1);
        if (fit) Surface.ZoomToFit();
        Surface.InvalidateVisual();
        RefreshSelectionUi();
        UpdateJobEstimate();
        UpdateTitle();
    }

    /// <summary>Wraps a user edit: snapshot for undo, apply, redraw.</summary>
    private void Edit(Action change)
    {
        _undo.Push(_project);
        change();
        MarkDirty();
        Surface.InvalidateVisual();
        RefreshSelectionUi();
        UpdateJobEstimate();
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() =>
        Title = $"{AppInfo.Product} {AppInfo.Version} – {(_filePath != null ? Path.GetFileName(_filePath) : "Untitled")}{(_dirty ? " *" : "")}";

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "Save changes to the current project?", "LaserForge", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(_filePath);
        return true;
    }

    // ================================================================== file menu

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var p = LaserProject.CreateDefault();
        p.Machine = _project.Machine.Clone(); // keep the user's machine setup
        _undo.Clear();
        _dirty = false;
        SetProject(p, null, fit: true);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = ProjectFilter };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var p = ProjectSerializer.Load(dlg.FileName);
            _undo.Clear();
            _dirty = false;
            SetProject(p, dlg.FileName, fit: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open project:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject(_filePath);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProject(null);

    private bool SaveProject(string? path)
    {
        if (path == null)
        {
            var dlg = new SaveFileDialog { Filter = ProjectFilter, DefaultExt = ProjectSerializer.FileExtension };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        ProjectSerializer.Save(_project, path);
        _filePath = path;
        _dirty = false;
        UpdateTitle();
        return true;
    }

    private void ImportImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ImageLoader.FileFilter };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var (gray, w, h) = ImageLoader.LoadGray(dlg.FileName);
            // Default: longest side 100 mm, clamped to the bed. Resize afterwards with the handles or W/H boxes.
            double longest = Math.Min(100, Math.Min(_project.Machine.BedWidthMm, _project.Machine.BedHeightMm) - 20);
            double wmm = w >= h ? longest : longest * w / h;
            double hmm = w >= h ? longest * h / w : longest;
            var img = new ImageShape
            {
                Name = Path.GetFileName(dlg.FileName),
                X = 10, Y = 10, WidthMm = wmm, HeightMm = hmm,
                PixelWidth = w, PixelHeight = h, Gray = gray,
            };
            Surface.AddShape(img);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load image:\n{ex.Message}", "Import image", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSvg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SVG files (*.svg)|*.svg" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var paths = SvgImporter.Import(File.ReadAllText(dlg.FileName));
            if (paths.Count == 0)
            {
                MessageBox.Show(this, "No drawable paths found. If the SVG contains text, convert it to paths first.", "Import SVG");
                return;
            }
            var shape = new VectorShape(paths, Path.GetFileName(dlg.FileName));
            var bed = Bounds.FromRect(0, 0, _project.Machine.BedWidthMm, _project.Machine.BedHeightMm);
            if (!bed.Contains(shape.GetBounds())) shape.MoveTo(10, 10);
            Surface.AddShape(shape);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not import SVG:\n{ex.Message}", "Import SVG", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportGcode_Click(object sender, RoutedEventArgs e)
    {
        var job = BuildJob();
        if (job == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = GcodeFilter,
            DefaultExt = ".gcode",
            FileName = Path.GetFileNameWithoutExtension(_filePath ?? "job") + ".gcode",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, job.Text);
        JobText.Text = $"Exported {job.Lines.Count:N0} lines, est. {FormatTime(job.EstimatedTime)}";
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ================================================================== edit / arrange

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

    private List<CoreShape> Selected() => Surface.SelectedShapes().ToList();

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
            MessageBox.Show(this, "Images can only be rotated in 90° steps.", "Rotate");
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
                ? $"{sel.Count} selected – {b.Width:0.#} × {b.Height:0.#} mm"
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

    // ================================================================== layers

    private void LayersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || Surface == null) return;
        var layer = ActiveLayer;
        if (layer == null) return;
        Surface.ActiveLayerId = layer.Id;
        _updatingUi = true;
        try
        {
            IntervalBox.Text = Num(layer.LineIntervalMm);
            OverscanBox.Text = Num(layer.OverscanMm);
            MinPowerBox.Text = Num(layer.MinPowerPercent);
            BidirCheck.IsChecked = layer.Bidirectional;
            AirCheck.IsChecked = layer.AirAssist;
        }
        finally { _updatingUi = false; }
    }

    private void LayerDetail_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || ActiveLayer is not { } layer) return;
        if (TryNum(IntervalBox.Text, out var iv)) layer.LineIntervalMm = Math.Clamp(iv, 0.02, 5);
        if (TryNum(OverscanBox.Text, out var os)) layer.OverscanMm = Math.Clamp(os, 0, 50);
        if (TryNum(MinPowerBox.Text, out var mp)) layer.MinPowerPercent = Math.Clamp(mp, 0, 100);
        layer.Bidirectional = BidirCheck.IsChecked == true;
        layer.AirAssist = AirCheck.IsChecked == true;
        MarkDirty();
        Surface.InvalidateVisual();
        UpdateJobEstimate();
    }

    private void LayersGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // Let the binding commit first, then sanitise and redraw.
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var l in _project.Layers)
            {
                l.SpeedMmMin = Math.Clamp(l.SpeedMmMin, 1, 100_000);
                l.PowerPercent = Math.Clamp(l.PowerPercent, 0, 100);
                l.Passes = Math.Clamp(l.Passes, 1, 100);
            }
            MarkDirty();
            Surface.InvalidateVisual();
            UpdateJobEstimate();
        }, DispatcherPriority.Background);
    }

    private void LayerColor_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Layer layer) return;
        int i = Array.IndexOf(Palette, layer.ColorHex.ToUpperInvariant());
        layer.ColorHex = Palette[(i + 1) % Palette.Length];
        LayersGrid.Items.Refresh();
        MarkDirty();
        Surface.InvalidateVisual();
        e.Handled = true;
    }

    private void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        var layer = new Layer
        {
            Name = $"Layer {_project.Layers.Count + 1}",
            ColorHex = Palette[_project.Layers.Count % Palette.Length],
            Mode = LayerMode.Line,
            SpeedMmMin = 1000,
            PowerPercent = 50,
        };
        Edit(() => _project.Layers.Add(layer));
        LayersGrid.Items.Refresh();
        LayersGrid.SelectedItem = layer;
    }

    private void RemoveLayer_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveLayer is not { } layer) return;
        if (_project.Layers.Count <= 1) { MessageBox.Show(this, "A project needs at least one layer.", "Layers"); return; }
        int count = _project.ShapesOn(layer).Count();
        if (count > 0 && MessageBox.Show(this, $"Layer '{layer.Name}' has {count} shape(s). Delete them too?", "Delete layer",
                                         MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        Edit(() =>
        {
            _project.Shapes.RemoveAll(s => s.LayerId == layer.Id);
            _project.Layers.Remove(layer);
        });
        LayersGrid.Items.Refresh();
        LayersGrid.SelectedIndex = 0;
    }

    private void LayerUp_Click(object sender, RoutedEventArgs e) => MoveLayer(-1);
    private void LayerDown_Click(object sender, RoutedEventArgs e) => MoveLayer(1);

    private void MoveLayer(int delta)
    {
        if (ActiveLayer is not { } layer) return;
        int i = _project.Layers.IndexOf(layer), j = i + delta;
        if (j < 0 || j >= _project.Layers.Count) return;
        Edit(() => (_project.Layers[i], _project.Layers[j]) = (_project.Layers[j], _project.Layers[i]));
        LayersGrid.Items.Refresh();
        LayersGrid.SelectedItem = layer;
    }

    private void AssignLayer_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveLayer is not { } layer) return;
        var sel = Selected();
        if (sel.Count == 0) return;
        var movable = sel.Where(s => s is VectorShape || layer.Mode == LayerMode.Image).ToList();
        if (movable.Count < sel.Count)
            MessageBox.Show(this, "Images can only go on Image-mode layers; they were left where they are.", "Layers");
        Edit(() => movable.ForEach(s => s.LayerId = layer.Id));
    }

    // ================================================================== tools menu / view

    private void MaterialTest_Click(object sender, RoutedEventArgs e)
    {
        var form = new FormDialog("Material test grid", this)
            .Number("Min speed (mm/min)", 1000).Number("Max speed (mm/min)", 5000).Number("Speed steps", 5)
            .Number("Min power %", 10).Number("Max power %", 100).Number("Power steps", 5)
            .Number("Cell size (mm)", 8).Number("Gap (mm)", 3)
            .Choice("Cell mode", new[] { LayerMode.Fill, LayerMode.Line }, LayerMode.Fill)
            .Check("Engrave labels", true);
        if (!form.Show(out var r)) return;
        if (!ConfirmDiscard()) return;
        var o = new MaterialTestOptions
        {
            MinSpeed = r.GetNumber("Min speed (mm/min)", 1000), MaxSpeed = r.GetNumber("Max speed (mm/min)", 5000),
            SpeedSteps = Math.Clamp(r.GetInt("Speed steps", 5), 1, 20),
            MinPower = r.GetNumber("Min power %", 10), MaxPower = r.GetNumber("Max power %", 100),
            PowerSteps = Math.Clamp(r.GetInt("Power steps", 5), 1, 20),
            CellSizeMm = r.GetNumber("Cell size (mm)", 8), GapMm = r.GetNumber("Gap (mm)", 3),
            CellMode = r.GetChoice<LayerMode>("Cell mode"),
            Labels = r.GetCheck("Engrave labels"),
        };
        _undo.Clear();
        _dirty = true;
        SetProject(MaterialTestGenerator.Create(_project.Machine, o), null, fit: true);
        Surface.SelectOnly(_project.Shapes.Select(s => s.Id));
    }

    private void MachineSettings_Click(object sender, RoutedEventArgs e)
    {
        var m = _project.Machine;
        var form = new FormDialog("Machine settings", this)
            .Text("Name", m.Name)
            .Number("Bed width (mm)", m.BedWidthMm).Number("Bed height (mm)", m.BedHeightMm)
            .Number("Max S value ($30)", m.MaxSpindleS).Number("Travel speed (mm/min)", m.TravelSpeedMmMin)
            .Check("Origin front-left (flip Y)", m.FlipY)
            .Check("Dynamic power M4 (recommended)", m.DynamicPower)
            .Check("Return to origin when done", m.ReturnToOrigin);
        if (!form.Show(out var r)) return;
        Edit(() =>
        {
            m.Name = r.GetText("Name");
            m.BedWidthMm = Math.Max(10, r.GetNumber("Bed width (mm)", m.BedWidthMm));
            m.BedHeightMm = Math.Max(10, r.GetNumber("Bed height (mm)", m.BedHeightMm));
            m.MaxSpindleS = Math.Max(1, r.GetNumber("Max S value ($30)", m.MaxSpindleS));
            m.TravelSpeedMmMin = Math.Max(100, r.GetNumber("Travel speed (mm/min)", m.TravelSpeedMmMin));
            m.FlipY = r.GetCheck("Origin front-left (flip Y)");
            m.DynamicPower = r.GetCheck("Dynamic power M4 (recommended)");
            m.ReturnToOrigin = r.GetCheck("Return to origin when done");
        });
        Surface.ZoomToFit();
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (Surface == null || sender is not FrameworkElement { Tag: string tag }) return;
        Surface.Tool = Enum.Parse<DesignTool>(tag);
    }

    private void SelectTool(DesignTool tool)
    {
        foreach (var rb in MainToolBar.Items.OfType<RadioButton>())
            if (rb.Tag as string == tool.ToString()) rb.IsChecked = true;
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Surface.ZoomToFit();

    private void PreviewRaster_Click(object sender, RoutedEventArgs e)
    {
        bool on = sender == PreviewMenu ? PreviewMenu.IsChecked : PreviewCheck.IsChecked == true;
        PreviewMenu.IsChecked = on;
        PreviewCheck.IsChecked = on;
        Surface.PreviewRaster = on;
        Surface.InvalidateVisual();
    }

    private void Snap_Click(object sender, RoutedEventArgs e) => Surface.SnapToGrid = SnapMenu.IsChecked;

    // ================================================================== job building

    private GcodeJob? BuildJob()
    {
        var only = SelectedOnlyCheck.IsChecked == true && Surface.Selection.Count > 0 ? Selected() : null;
        var job = GcodeGenerator.Generate(_project, only);
        if (job.Bounds.IsEmpty)
        {
            MessageBox.Show(this, "Nothing to burn – check that shapes exist on layers with Output ticked.", "LaserForge");
            return null;
        }
        var bed = Bounds.FromRect(0, 0, _project.Machine.BedWidthMm, _project.Machine.BedHeightMm);
        if (!bed.Contains(job.Bounds) &&
            MessageBox.Show(this, "Part of the job is outside the bed and may hit the machine limits. Continue anyway?",
                            "Outside work area", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return null;
        return job;
    }

    /// <summary>Recomputes the time estimate in the background so big rasters don't freeze the UI.</summary>
    private void UpdateJobEstimate()
    {
        int version = ++_estimateVersion;
        var snapshot = ProjectSerializer.DeepClone(_project);
        Task.Run(() => GcodeGenerator.Generate(snapshot)).ContinueWith(t => Ui(() =>
        {
            if (version != _estimateVersion || t.IsFaulted) return;
            var job = t.Result;
            JobText.Text = job.Bounds.IsEmpty ? "" :
                $"Job {job.Bounds.Width:0.#} × {job.Bounds.Height:0.#} mm · est. {FormatTime(job.EstimatedTime)} · {job.Lines.Count:N0} lines";
        }));
    }

    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ================================================================== laser connection

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var ports = SerialTransport.AvailablePorts();
        PortCombo.ItemsSource = ports;
        if (ports.Length > 0) PortCombo.SelectedIndex = ports.Length - 1; // USB adapters usually get the highest COM number
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_transport != null) { Disconnect(); return; }
        if (PortCombo.SelectedItem is not string port) { MessageBox.Show(this, "Choose a COM port first.", "Connect"); return; }
        if (!int.TryParse(BaudCombo.Text, out int baud)) baud = 115200;
        try
        {
            _transport = new SerialTransport(port, baud);
            _transport.Open();
        }
        catch (Exception ex)
        {
            _transport?.Dispose();
            _transport = null;
            MessageBox.Show(this, $"Could not open {port}:\n{ex.Message}", "Connect", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _streamer = new GrblStreamer(_transport, _project.Machine.RxBufferSize);
        // Raster jobs acknowledge thousands of lines a second; only repaint the progress bar every 25 lines.
        _streamer.ProgressChanged += (ack, total) => { if (ack % 25 == 0 || ack >= total) Ui(() =>
        {
            JobProgress.Maximum = Math.Max(1, total);
            JobProgress.Value = ack;
            JobProgressText.Text = $"{ack:N0} / {total:N0} lines ({(total == 0 ? 0 : 100.0 * ack / total):0}%)";
        }); };
        _streamer.StateChanged += s => Ui(() => OnStreamState(s));
        _streamer.ErrorReported += (line, err) => Ui(() => Log($"!! {err}   (line: {line})"));
        _streamer.MessageReceived += msg => Ui(() => Log(msg));
        _streamer.StatusReceived += st => Ui(() => OnStatus(st));
        _statusTimer.Start();
        ConnectButton.Content = "Disconnect";
        Log($"Connected to {port} @ {baud}");
    }

    private void Disconnect()
    {
        if (_streamer?.IsBusy == true)
        {
            if (MessageBox.Show(this, "A job is running. Stop it and disconnect?", "Disconnect", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _streamer.Stop();
        }
        _statusTimer.Stop();
        _transport?.Dispose();
        _transport = null;
        _streamer = null;
        Surface.LaserPosition = null;
        Surface.InvalidateVisual();
        ConnectButton.Content = "Connect";
        MachineText.Text = "Not connected";
        OnStreamState(StreamState.Idle);
        Log("Disconnected");
    }

    private void Ui(Action a) => Dispatcher.InvokeAsync(a);

    private void OnStatus(GrblStatus st)
    {
        var pos = st.WorkPos ?? st.MachinePos;
        MachineText.Text = pos is { } p ? $"{st.State}  X{p.X:0.00} Y{p.Y:0.00}" : st.State;
        if (pos is { } mp)
        {
            var m = _project.Machine;
            Surface.LaserPosition = m.FlipY ? new Vec2(mp.X, m.BedHeightMm - mp.Y) : mp;
            Surface.InvalidateVisual();
        }
    }

    private void OnStreamState(StreamState s)
    {
        bool busy = s is StreamState.Running or StreamState.Paused;
        StartButton.IsEnabled = !busy;
        PauseButton.IsEnabled = busy;
        PauseButton.Content = s == StreamState.Paused ? "▶ Resume" : "❚❚ Pause";
        JobProgressText.Text = s switch
        {
            StreamState.Completed => "Done ✓",
            StreamState.Stopped => "Stopped",
            StreamState.Faulted => "Stopped on error – see console",
            _ => JobProgressText.Text,
        };
        if (s != StreamState.Idle) Log($"-- job {s.ToString().ToLowerInvariant()}");
    }

    private bool RequireConnection()
    {
        if (_streamer != null) return true;
        MessageBox.Show(this, "Connect to the laser first (Laser tab).", "LaserForge");
        return false;
    }

    private void Send(string command)
    {
        if (!RequireConnection()) return;
        if (_streamer!.SendCommand(command)) Log("> " + command);
        else Log("(busy – command not sent)");
    }

    private void Home_Click(object sender, RoutedEventArgs e) => Send("$H");
    private void Unlock_Click(object sender, RoutedEventArgs e) => Send("$X");
    private void SetOrigin_Click(object sender, RoutedEventArgs e) => Send("G10 L20 P1 X0 Y0");

    private void Jog_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        double step = double.TryParse((JogStepCombo.SelectedItem as ComboBoxItem)?.Content as string, NumberStyles.Float,
                                      CultureInfo.InvariantCulture, out var st) ? st : 10;
        double feed = TryNum(JogFeedBox.Text, out var f) ? f : 3000;
        if (!_streamer!.Jog(parts[0] * step, parts[1] * step, feed)) Log("(busy – jog ignored)");
    }

    private void JogCancel_Click(object sender, RoutedEventArgs e) => _streamer?.CancelJog();

    private void Frame_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || _streamer!.IsBusy) return;
        var bounds = SelectedOnlyCheck.IsChecked == true && Surface.Selection.Count > 0 ? Surface.SelectionBounds() : _project.OutputBounds();
        double power = TryNum(FramePowerBox.Text, out var p) ? Math.Clamp(p, 0, 5) : 0; // capped: framing must never mark
        double speed = TryNum(FrameSpeedBox.Text, out var s) ? s : 3000;
        var frame = GcodeGenerator.GenerateFrame(_project, power, speed, bounds);
        if (frame.Bounds.IsEmpty) { Log("Nothing to frame"); return; }
        Log($"Framing {frame.Bounds.Width:0.#} × {frame.Bounds.Height:0.#} mm at {power:0.#}% power");
        _streamer.Start(frame.Lines);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || _streamer!.IsBusy) return;
        var job = BuildJob();
        if (job == null) return;
        var ok = MessageBox.Show(this,
            $"Start burning?\n\nSize {job.Bounds.Width:0.#} × {job.Bounds.Height:0.#} mm, estimated {FormatTime(job.EstimatedTime)}.\n\n" +
            "Wear safety glasses rated for your laser's wavelength, keep a fire extinguisher nearby and never leave the laser unattended.",
            "Start job", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        Log($"Starting job: {job.Lines.Count:N0} lines");
        _streamer.Start(job.Lines);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_streamer == null) return;
        if (_streamer.State == StreamState.Paused) _streamer.Resume();
        else _streamer.Pause();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _streamer?.Stop();
        Log("STOP sent (feed hold + soft reset). You may need to Unlock ($X) or Home afterwards.");
    }

    private void SendCommand_Click(object sender, RoutedEventArgs e)
    {
        var cmd = CommandBox.Text.Trim();
        if (cmd.Length == 0) return;
        Send(cmd);
        CommandBox.Clear();
    }

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendCommand_Click(sender, e); e.Handled = true; }
    }

    private void Log(string text)
    {
        ConsoleList.Items.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (ConsoleList.Items.Count > 1000) ConsoleList.Items.RemoveAt(0);
        ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
    }

    // ================================================================== keyboard & closing

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl)
        {
            Action? a = e.Key switch
            {
                Key.N => () => New_Click(this, e),
                Key.O => () => Open_Click(this, e),
                Key.S => () => Save_Click(this, e),
                Key.I => () => ImportImage_Click(this, e),
                Key.E => () => ExportGcode_Click(this, e),
                _ => null,
            };
            if (a != null) { a(); e.Handled = true; return; }
        }

        // Leave ordinary typing alone in text boxes and grid cells.
        if (Keyboard.FocusedElement is TextBox or ComboBox or DataGridCell) return;

        double step = shift ? 10 : ctrl ? 0.1 : 1;
        switch (e.Key)
        {
            case Key.Z when ctrl: Undo_Click(this, e); break;
            case Key.Y when ctrl: Redo_Click(this, e); break;
            case Key.D when ctrl: Duplicate_Click(this, e); break;
            case Key.A when ctrl: SelectAll_Click(this, e); break;
            case Key.Delete: case Key.Back: Delete_Click(this, e); break;
            case Key.Escape: Surface.CancelDraft(); SelectTool(DesignTool.Select); break;
            case Key.Enter: Surface.FinishPen(closed: false); break;
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, -step); break;
            case Key.Down: Nudge(0, step); break;
            case Key.V when !ctrl: SelectTool(DesignTool.Select); break;
            case Key.R when !ctrl: SelectTool(DesignTool.Rectangle); break;
            case Key.E when !ctrl: SelectTool(DesignTool.Ellipse); break;
            case Key.G when !ctrl: SelectTool(DesignTool.Polygon); break;
            case Key.L when !ctrl: SelectTool(DesignTool.Line); break;
            case Key.P when !ctrl: SelectTool(DesignTool.Pen); break;
            case Key.T when !ctrl: SelectTool(DesignTool.Text); break;
            case Key.F when !ctrl: Surface.ZoomToFit(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_streamer?.IsBusy == true &&
            MessageBox.Show(this, "A job is running. Stop it and quit?", "LaserForge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        _streamer?.Stop();
        _statusTimer.Stop();
        _transport?.Dispose();
    }

    // ================================================================== help menu

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(this).ShowDialog();

    private void GitHub_Click(object sender, RoutedEventArgs e) => AboutWindow.OpenUrl(AppInfo.RepositoryUrl);

    private void ReportIssue_Click(object sender, RoutedEventArgs e) =>
        AboutWindow.OpenUrl($"{AppInfo.RepositoryUrl}/issues/new?body={Uri.EscapeDataString("\n\n---\n" + AboutWindow.DetailsText())}");

    // ================================================================== helpers

    private static string Num(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    private static bool TryNum(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
