using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.App;

// Layers: the layer list, per-layer settings (incl. fill angle, cross-hatch, kerf and tabs) and material presets.
public partial class MainWindow
{
    private MaterialLibrary? _materials;

    private void LayersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || Surface == null) return;
        if (ActiveLayer is { } layer) Surface.ActiveLayerId = layer.Id;
        RefreshLayerDetails();
    }

    private void RefreshLayerDetails()
    {
        if (ActiveLayer is not { } layer) return;
        _updatingUi = true;
        try
        {
            IntervalBox.Text = Num(layer.LineIntervalMm);
            OverscanBox.Text = Num(layer.OverscanMm);
            MinPowerBox.Text = Num(layer.MinPowerPercent);
            BidirCheck.IsChecked = layer.Bidirectional;
            AirCheck.IsChecked = layer.AirAssist;
            FillAngleBox.Text = Num(layer.FillAngleDeg);
            CrossHatchCheck.IsChecked = layer.CrossHatch;
            KerfBox.Text = Num(layer.KerfMm);
            TabCountBox.Text = layer.TabCount.ToString();
            TabWidthBox.Text = Num(layer.TabWidthMm);

            // Only show the settings that matter for this layer's mode.
            bool raster = layer.Mode != LayerMode.Line;
            FillOptions.Visibility = layer.Mode == LayerMode.Fill ? Visibility.Visible : Visibility.Collapsed;
            CutOptions.Visibility = layer.Mode == LayerMode.Line ? Visibility.Visible : Visibility.Collapsed;
            IntervalBox.IsEnabled = OverscanBox.IsEnabled = BidirCheck.IsEnabled = raster;
            MinPowerBox.IsEnabled = layer.Mode == LayerMode.Image;
            MaterialText.Text = layer.MaterialName != null ? $"Material: {layer.MaterialName}" : "";
            MaterialText.Visibility = layer.MaterialName != null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingUi = false; }
    }

    private void LayerDetail_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || ActiveLayer is not { } layer) return;
        if (TryNum(IntervalBox.Text, out var iv)) layer.LineIntervalMm = Math.Clamp(iv, 0.02, 5);
        if (TryNum(OverscanBox.Text, out var os)) layer.OverscanMm = Math.Clamp(os, 0, 50);
        if (TryNum(MinPowerBox.Text, out var mp)) layer.MinPowerPercent = Math.Clamp(mp, 0, 100);
        if (TryNum(FillAngleBox.Text, out var fa)) layer.FillAngleDeg = Math.Clamp(fa, -360, 360);
        if (TryNum(KerfBox.Text, out var kerf)) layer.KerfMm = Math.Clamp(kerf, 0, 2);
        if (TryNum(TabCountBox.Text, out var tabs)) layer.TabCount = (int)Math.Clamp(Math.Round(tabs), 0, 50);
        if (TryNum(TabWidthBox.Text, out var tw)) layer.TabWidthMm = Math.Clamp(tw, 0.1, 20);
        layer.Bidirectional = BidirCheck.IsChecked == true;
        layer.AirAssist = AirCheck.IsChecked == true;
        layer.CrossHatch = CrossHatchCheck.IsChecked == true;
        ExitSimulation();
        MarkDirty();
        RefreshLayerDetails();
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
            ExitSimulation();
            MarkDirty();
            RefreshLayerDetails();
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
        if (_project.Layers.Count <= 1) { ShowToast("A project needs at least one layer.", ToastKind.Info); return; }
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
            ShowToast("Images can only go on Image-mode layers; they were left where they are.", ToastKind.Warning);
        Edit(() => movable.ForEach(s => s.LayerId = layer.Id));
    }

    // ================================================================== material library

    private MaterialLibrary Materials => _materials ??= MaterialLibrary.Load(AppSettings.MaterialsPath);

    private void MaterialLibrary_Click(object sender, RoutedEventArgs e)
    {
        var layer = ActiveLayer;
        var win = new MaterialLibraryWindow(this, Materials, AppSettings.MaterialsPath, layer);
        if (win.ShowDialog() != true || win.Applied is not { } preset || layer == null) return;
        Edit(() => preset.ApplyTo(layer));
        LayersGrid.Items.Refresh();
        LayersGrid.SelectedItem = layer;
        RefreshLayerDetails();
        ShowToast($"Applied {preset.DisplayName} to '{layer.Name}'");
    }
}
