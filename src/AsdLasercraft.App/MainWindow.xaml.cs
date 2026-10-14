using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AsdLasercraft.App.Controls;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Serialization;
using CoreShape = AsdLasercraft.Core.Model.Shape;
using Vec2 = AsdLasercraft.Core.Geometry.Vec2;

namespace AsdLasercraft.App;

/// <summary>
/// Main window. The code is split by area into partial classes:
/// <list type="bullet">
/// <item>MainWindow.xaml.cs – project state, undo, shared helpers (this file)</item>
/// <item>MainWindow.File.cs – new/open/save, import, export, recent files, drag and drop</item>
/// <item>MainWindow.Edit.cs – selection, transforms, text, arrays, image settings and tracing</item>
/// <item>MainWindow.Layers.cs – layer list, layer settings and material presets</item>
/// <item>MainWindow.Laser.cs – connection, machine control, job origin, running jobs</item>
/// <item>MainWindow.Preview.cs – job building, pre-flight checks and the simulated preview</item>
/// <item>MainWindow.Tools.cs – generators, machine settings, view options, updates, keyboard</item>
/// </list>
/// </summary>
public partial class MainWindow : Window
{
    private const string ProjectFilter = "A·S·D Lasercraft project (*.asdl)|*.asdl";
    private const string GcodeFilter = "G-code (*.gcode;*.nc)|*.gcode;*.nc|All files|*.*";
    private static readonly string[] Palette =
    {
        "#4C8DFF", "#FF4D5E", "#3DDC97", "#C27BFF", "#FFB020", "#2ED3D3",
        "#FF6FB5", "#B8D84A", "#9AA3B5", "#7B9CFF", "#FF8A4C", "#E8EBF1",
    };

    private readonly AppSettings _settings;
    private readonly bool _smoke;
    private LaserProject _project = LaserProject.CreateDefault();
    private readonly UndoStack _undo = new();
    private string? _filePath;
    private bool _dirty;
    private bool _updatingUi;
    private int _estimateVersion;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    public MainWindow(AppSettings settings) : this(settings, smoke: false) { }

    internal MainWindow(AppSettings settings, bool smoke)
    {
        _settings = settings;
        _smoke = smoke;
        _updatingUi = true;
        InitializeComponent();
        _updatingUi = false;
        WindowTheme.ApplyDark(this);

        ModeColumn.ItemsSource = Enum.GetValues<LayerMode>();
        DitherCombo.ItemsSource = Enum.GetValues<DitherMode>();
        OriginCombo.ItemsSource = Enum.GetValues<JobOrigin>().Select(JobSettings.Describe).ToList();

        Surface.BeforeChange += () => { _undo.Push(_project); MarkDirty(); };
        Surface.Changed += () => { RefreshSelectionUi(); UpdateJobEstimate(); };
        Surface.SelectionChanged += RefreshSelectionUi;
        Surface.CursorMoved += p => { CursorText.Text = $"X {p.X:0.0}  Y {p.Y:0.0} mm"; UpdateZoomText(); };
        Surface.TextRequested += OnTextRequested;
        Surface.FilesDropped += OnFilesDropped;
        Surface.MouseWheel += (_, _) => UpdateZoomText();
        Surface.SizeChanged += (_, _) => UpdateZoomText();
        Surface.SnapToGrid = settings.SnapToGrid;
        Surface.ShowRulers = settings.ShowRulers;
        _toastTimer.Tick += (_, _) => HideToast();

        InitLaser();

        _updatingUi = true;
        SnapMenu.IsChecked = settings.SnapToGrid;
        RulersMenu.IsChecked = settings.ShowRulers;
        SplashMenu.IsChecked = settings.ShowSplash;
        AutoUpdateMenu.IsChecked = settings.CheckForUpdates;
        _updatingUi = false;

        var project = LaserProject.CreateDefault();
        project.Machine = settings.Machine.Clone();
        SetProject(project, null, fit: true);
        RefreshPorts();
        RefreshRecentMenu();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Surface.ZoomToFit();
        UpdateZoomText();
        Surface.Focus();
        if (!_smoke && _settings.CheckForUpdates) await CheckForUpdatesAsync(userAsked: false);
    }

    // ================================================================== project state

    private Layer? ActiveLayer => LayersGrid.SelectedItem as Layer;

    private void SetProject(LaserProject project, string? path, bool fit)
    {
        ExitSimulation();
        int layerIndex = Math.Max(0, LayersGrid.SelectedIndex);
        project.Job ??= new JobSettings();
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
        RefreshOriginUi();
        RefreshSelectionUi();
        UpdateJobEstimate();
        UpdateTitle();
    }

    /// <summary>Wraps a user edit: snapshot for undo, apply, redraw.</summary>
    private void Edit(Action change)
    {
        ExitSimulation();
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
        Title = $"{AppInfo.Product} {AppInfo.Version} – {(_filePath != null ? Path.GetFileName(_filePath) : "Untitled")}{(_dirty ? " •" : "")}";

    private bool ConfirmDiscard()
    {
        if (!_dirty || _smoke) return true;
        var r = MessageBox.Show(this, "Save changes to the current project?", AppInfo.Product, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(_filePath);
        return true;
    }

    private List<CoreShape> Selected() => Surface.SelectedShapes().ToList();

    /// <summary>Recomputes the time estimate in the background so big rasters don't freeze the UI.</summary>
    private void UpdateJobEstimate()
    {
        UpdateAnchorMarker();
        int version = ++_estimateVersion;
        var snapshot = ProjectSerializer.DeepClone(_project);
        Task.Run(() => GcodeGenerator.Generate(snapshot)).ContinueWith(t => Ui(() =>
        {
            if (version != _estimateVersion || t.IsFaulted) return;
            var job = t.Result;
            JobText.Text = job.Bounds.IsEmpty ? "Nothing to burn yet" :
                $"Job {job.Bounds.Width:0.#} × {job.Bounds.Height:0.#} mm  ·  est. {FormatTime(job.EstimatedTime)}  ·  {job.Lines.Count:N0} lines";
        }));
    }

    // 100 % = real size on a 96-DPI screen (3.78 px per mm).
    private void UpdateZoomText() => ZoomText.Text = $"{Surface.Zoom / (96 / 25.4) * 100:0}%";

    // ================================================================== toast

    private enum ToastKind { Success, Info, Warning, Error }

    private void ShowToast(string message, ToastKind kind = ToastKind.Success)
    {
        var (icon, brush) = kind switch
        {
            ToastKind.Error => ("I.Error", "Brush.Danger"),
            ToastKind.Warning => ("I.Warning", "Brush.Warning"),
            ToastKind.Info => ("I.Info", "Brush.Info"),
            _ => ("I.Success", "Brush.Success"),
        };
        ToastGlyph.Data = (Geometry)FindResource(icon);
        ToastGlyph.Foreground = (Brush)FindResource(brush);
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => { if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    // ================================================================== helpers

    private void Ui(Action a) => Dispatcher.InvokeAsync(a);

    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string Num(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    private static bool TryNum(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static void SetGlyph(Glyph glyph, string iconKey, string text)
    {
        glyph.Data = (Geometry)Application.Current.FindResource(iconKey);
        glyph.Text = text;
    }
}
