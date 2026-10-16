using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Generators;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;
using Microsoft.Win32;

namespace AsdLasercraft.App.Dialogs;

/// <summary>
/// Reads a photo of a burned material test grid with Claude's vision and turns the best cell into a material
/// preset. The grid layout comes from the app (the last grid generated), so the speeds and powers are exact.
/// </summary>
public partial class TestGridWindow : Window
{
    private static readonly (GridGoal Goal, string Label)[] Goals =
    {
        (GridGoal.Engrave, "Darkest clean engrave"),
        (GridGoal.LightEngrave, "Light, fast engrave"),
        (GridGoal.Photo, "Photo engraving (smooth mid-tones)"),
        (GridGoal.Cut, "Cut all the way through"),
    };

    private readonly AiServices _ai;
    private readonly AppSettings _settings;
    private readonly MaterialLibrary _materials;
    private readonly string _materialsPath;
    private byte[]? _photo;
    private GridReading? _reading;
    private MaterialTestOptions? _readGrid;
    private GridGoal _readGoal;

    /// <summary>The preset the user chose to put on the current layer, if any.</summary>
    public MaterialPreset? ApplyToLayer { get; private set; }

    public TestGridWindow(Window owner, AiServices ai, AppSettings settings, MaterialLibrary materials, string materialsPath, Layer? activeLayer)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        WindowTheme.ApplyDark(this);
        _ai = ai;
        _settings = settings;
        _materials = materials;
        _materialsPath = materialsPath;

        var g = settings.LastTestGrid ?? new MaterialTestOptions();
        MinSpeedBox.Text = Fmt(g.MinSpeed); MaxSpeedBox.Text = Fmt(g.MaxSpeed); SpeedStepsBox.Text = g.SpeedSteps.ToString();
        MinPowerBox.Text = Fmt(g.MinPower); MaxPowerBox.Text = Fmt(g.MaxPower); PowerStepsBox.Text = g.PowerSteps.ToString();
        LineCellsCheck.IsChecked = g.CellMode == LayerMode.Line;
        GridHint.Text = settings.LastTestGrid != null
            ? "Filled in from the last grid you generated. Change these if you burned a different one."
            : "Enter the values you used in Tools → Material test grid (these are its defaults).";

        GoalCombo.ItemsSource = Goals.Select(x => x.Label).ToList();
        GoalCombo.SelectedIndex = g.CellMode == LayerMode.Line ? 3 : 0;
        // "Plywood (birch) 3 mm – Engrave" → material "Plywood (birch)", thickness 3.
        if (activeLayer?.MaterialName is { } m)
        {
            var name = m.Split('–')[0].Trim();
            var thick = System.Text.RegularExpressions.Regex.Match(name, @"\s+(\d+(?:[.,]\d+)?)\s*mm$");
            if (thick.Success)
            {
                ThicknessBox.Text = thick.Groups[1].Value;
                name = name[..thick.Index].Trim();
            }
            MaterialBox.Text = name;
        }
        ApplyButton.ToolTip = activeLayer != null ? $"Put these settings on layer '{activeLayer.Name}'" : "Select a layer first";
        ApplyButton.Tag = activeLayer != null;
    }

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

    private static bool TryNum(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ================================================================== photo

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ImageLoader.FileFilter };
        if (dlg.ShowDialog(this) == true) LoadPhoto(dlg.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) LoadPhoto(files[0]);
    }

    private void LoadPhoto(string path)
    {
        try
        {
            var (jpeg, preview) = PrepareJpeg(path);
            SetPhoto(jpeg, preview);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open that photo:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void SetPhoto(byte[] jpeg, BitmapSource preview)
    {
        _photo = jpeg;
        PhotoImage.Source = preview;
        PhotoPlaceholder.Visibility = Visibility.Collapsed;
        ChangePhotoButton.Visibility = Visibility.Visible;
        AnalyseButton.IsEnabled = true;
    }

    /// <summary>Scales the photo to at most 1568 px on its long side (plenty for the model, quick to send) as JPEG.</summary>
    public static (byte[] Jpeg, BitmapSource Preview) PrepareJpeg(string path)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(path, UriKind.Absolute);
        src.EndInit();
        src.Freeze();
        BitmapSource bmp = src;
        double scale = Math.Min(1, 1568.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        if (scale < 1) bmp = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        if (bmp.Format != PixelFormats.Bgr24 && bmp.Format != PixelFormats.Bgra32) bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        bmp.Freeze();
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return (ms.ToArray(), bmp);
    }

    // ================================================================== analyse

    private MaterialTestOptions? ReadGridSettings()
    {
        if (!TryNum(MinSpeedBox.Text, out var s0) || !TryNum(MaxSpeedBox.Text, out var s1) || !TryNum(SpeedStepsBox.Text, out var sn) ||
            !TryNum(MinPowerBox.Text, out var p0) || !TryNum(MaxPowerBox.Text, out var p1) || !TryNum(PowerStepsBox.Text, out var pn))
        {
            MessageBox.Show(this, "The grid settings must all be numbers.", Title);
            return null;
        }
        var g = (_settings.LastTestGrid ?? new MaterialTestOptions());
        return new MaterialTestOptions
        {
            MinSpeed = s0, MaxSpeed = s1, SpeedSteps = Math.Clamp((int)Math.Round(sn), 1, 20),
            MinPower = p0, MaxPower = p1, PowerSteps = Math.Clamp((int)Math.Round(pn), 1, 20),
            CellSizeMm = g.CellSizeMm, GapMm = g.GapMm, Labels = g.Labels, LineIntervalMm = g.LineIntervalMm,
            CellMode = LineCellsCheck.IsChecked == true ? LayerMode.Line : LayerMode.Fill,
        };
    }

    private GridGoal SelectedGoal => Goals[Math.Max(0, GoalCombo.SelectedIndex)].Goal;

    private async void Analyse_Click(object sender, RoutedEventArgs e)
    {
        if (_photo == null || ReadGridSettings() is not { } grid) return;
        if (!_settings.AiPhotoConsent)
        {
            var ok = MessageBox.Show(this,
                "To read the grid, this photo will be sent to Anthropic's Claude API for analysis. Nothing else is sent.\n\nAllow this? (You can change it later in AI settings.)",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
            _settings.AiPhotoConsent = true;
            _settings.Save();
        }

        var goal = SelectedGoal;
        AnalyseButton.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var reading = await new TestGridReader(_ai.Client).ReadAsync(_photo, "image/jpeg", grid, goal, MaterialBox.Text);
            ShowReading(reading, grid, goal);
        }
        catch (AiException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            AnalyseButton.IsEnabled = true;
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    internal void ShowReading(GridReading reading, MaterialTestOptions grid, GridGoal goal)
    {
        _reading = reading;
        _readGrid = grid;
        _readGoal = goal;
        ResultTitle.Text = reading.Summary;
        ResultConfidence.Text = $"Row {reading.Row + 1}, column {reading.Column + 1}  ·  {(reading.Confidence >= 0.75 ? "confident" : reading.Confidence >= 0.45 ? "fairly sure" : "not sure – check it yourself")} ({reading.Confidence:P0})";
        ReasonText.Text = reading.Reasoning;
        AdviceText.Text = string.IsNullOrWhiteSpace(reading.Advice) ? "" : "Tip: " + reading.Advice;
        AdviceText.Visibility = string.IsNullOrWhiteSpace(reading.Advice) ? Visibility.Collapsed : Visibility.Visible;
        if (reading.Alternative is { } alt)
            AltText.Text = $"Runner-up: {TestGridReader.Speed(grid, alt.Row):0} mm/min at {TestGridReader.Power(grid, alt.Column):0.#}% (outlined).";
        else AltText.Text = "";
        BuildCellGrid(reading, grid);
        ResultPanel.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = true;
        ApplyButton.IsEnabled = ApplyButton.Tag is true;
    }

    /// <summary>A small map of the grid with the chosen cell filled and the runner-up outlined.</summary>
    private void BuildCellGrid(GridReading r, MaterialTestOptions g)
    {
        CellGrid.Children.Clear();
        CellGrid.RowDefinitions.Clear();
        CellGrid.ColumnDefinitions.Clear();
        int rows = g.SpeedSteps, cols = g.PowerSteps;
        double cell = Math.Clamp(220.0 / Math.Max(rows, cols), 12, 30);
        CellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < cols; c++) CellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell + 3) });
        CellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < rows; i++) CellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cell + 3) });

        TextBlock AxisLabel(string t) { var tb = new TextBlock { Text = t, FontSize = 9.5, Margin = new Thickness(0, 0, 4, 2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }; tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim"); return tb; }
        for (int c = 0; c < cols; c++)
        {
            var l = AxisLabel($"{TestGridReader.Power(g, c):0}%");
            Grid.SetColumn(l, c + 1);
            CellGrid.Children.Add(l);
        }
        for (int i = 0; i < rows; i++)
        {
            var l = AxisLabel($"{TestGridReader.Speed(g, i):0}");
            l.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(l, i + 1);
            CellGrid.Children.Add(l);
            for (int c = 0; c < cols; c++)
            {
                bool best = i == r.Row && c == r.Column;
                bool alt = r.Alternative is { } a && a.Row == i && a.Column == c;
                // Darker towards high power / low speed, like the real burn.
                double heat = (c + 1.0) / cols * (1 - 0.6 * i / Math.Max(1, rows - 1));
                byte v = (byte)(200 - 150 * heat);
                var b = new Border
                {
                    Width = cell, Height = cell, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.FromRgb((byte)(v + 30), (byte)(v + 10), v)),
                    BorderThickness = new Thickness(best || alt ? 2.5 : 0),
                    ToolTip = $"{TestGridReader.Speed(g, i):0} mm/min, {TestGridReader.Power(g, c):0.#}%",
                };
                if (best) b.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
                else if (alt) b.SetResourceReference(Border.BorderBrushProperty, "Brush.Info");
                Grid.SetRow(b, i + 1);
                Grid.SetColumn(b, c + 1);
                CellGrid.Children.Add(b);
            }
        }
    }

    // ================================================================== results

    private MaterialPreset? CurrentPreset()
    {
        if (_reading == null || _readGrid == null) return null;
        double thickness = TryNum(ThicknessBox.Text, out var t) ? Math.Clamp(t, 0, 100) : 0;
        return TestGridReader.ToPreset(_reading, _readGrid, _readGoal, MaterialBox.Text, thickness);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPreset() is not { } preset) return;
        _materials.Presets.Add(preset);
        try { _materials.Save(_materialsPath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the material library:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SaveButton.IsEnabled = false;
        SaveButton.ToolTip = "Saved";
        MessageBox.Show(this, $"Saved \"{preset.DisplayName}\" ({preset.Summary}) to your material library.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPreset() is not { } preset) return;
        ApplyToLayer = preset;
        DialogResult = true;
    }
}
