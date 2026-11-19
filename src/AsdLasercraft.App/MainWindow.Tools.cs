using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AsdLasercraft.App.Controls;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core;
using AsdLasercraft.Core.Generators;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.App;

// Tools and View menus, machine settings, help/updates, keyboard shortcuts and closing.
public partial class MainWindow
{
    private UpdateInfo? _update;

    // ================================================================== generators

    private void MaterialTest_Click(object sender, RoutedEventArgs e)
    {
        var form = new FormDialog("Material test grid", this)
            .Hint("Builds a labelled speed × power grid for finding the right settings on a new material. This replaces the current project.")
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
        _settings.LastTestGrid = o; // so AI → Read a test grid photo knows this grid's layout
        _settings.Save();
        _undo.Clear();
        _dirty = true;
        SetProject(MaterialTestGenerator.Create(_project.Machine, o), null, fit: true);
        Surface.SelectOnly(_project.Shapes.Select(s => s.Id));
    }

    private void ScanOffsetTest_Click(object sender, RoutedEventArgs e)
    {
        var form = new FormDialog("Scan offset test card", this)
            .Hint("Engraves rows of thin bars at different speeds with compensation switched off. Under a magnifier, measure how far the " +
                  "bar edges are smeared at each speed and enter those values in Machine settings → Scan offsets. This replaces the current project.")
            .Text("Speeds (mm/min)", "1000, 2000, 3000, 4500, 6000")
            .Number("Power %", 30)
            .Number("Line interval (mm)", 0.1);
        if (!form.Show(out var r)) return;
        var speeds = r.GetText("Speeds (mm/min)").Split(',', ';', ' ')
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Where(v => v > 0).Take(12).ToArray();
        if (speeds.Length == 0) { ShowToast("Enter at least one speed.", ToastKind.Warning); return; }
        if (!ConfirmDiscard()) return;
        var o = new ScanOffsetTestOptions
        {
            Speeds = speeds,
            PowerPercent = Math.Clamp(r.GetNumber("Power %", 30), 1, 100),
            LineIntervalMm = Math.Clamp(r.GetNumber("Line interval (mm)", 0.1), 0.02, 1),
        };
        _undo.Clear();
        _dirty = true;
        var p = ScanOffsetTestGenerator.Create(_project.Machine, o);
        p.Machine.ScanOffsets = _project.Machine.ScanOffsets.Select(x => new ScanOffsetPoint(x.SpeedMmMin, x.OffsetMm)).ToList();
        SetProject(p, null, fit: true);
        ShowToast("Scan offset card ready. It burns with compensation off regardless of your table.", ToastKind.Info);
    }

    // ================================================================== machine settings

    private void MachineSettings_Click(object sender, RoutedEventArgs e)
    {
        var m = _project.Machine;
        var form = new FormDialog("Machine settings", this)
            .Section("Machine")
            .Text("Name", m.Name)
            .Number("Bed width (mm)", m.BedWidthMm).Number("Bed height (mm)", m.BedHeightMm)
            .Number("Max S value ($30)", m.MaxSpindleS, "GRBL $30 – usually 1000, sometimes 255.")
            .Number("Travel speed (mm/min)", m.TravelSpeedMmMin)
            .Check("Origin front-left (flip Y)", m.FlipY)
            .Check("Dynamic power M4 (recommended)", m.DynamicPower)
            .Check("Return to origin when done", m.ReturnToOrigin)
            .Section("Scan offset (bidirectional engraving)")
            .Hint("If engraved edges look fuzzy or doubled, list the blur width at each speed as speed:offset, e.g. 1000:0.05, 3000:0.15, 6000:0.3. " +
                  "Leave empty to switch compensation off. Tools → Scan offset test card helps you measure it.")
            .Text("Scan offsets", m.ScanOffsetsText())
            .Section("Simulator")
            .Number("Simulator speed (×)", _settings.SimulatorSpeed, "How much faster than real time the built-in simulator runs jobs.");
        if (!form.Show(out var r)) return;
        if (!MachineProfile.TryParseScanOffsets(r.GetText("Scan offsets"), out var offsets))
        {
            MessageBox.Show(this, "Scan offsets should look like 1000:0.05, 3000:0.15 (speed in mm/min : offset in mm).", "Machine settings",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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
            m.ScanOffsets = offsets;
        });
        _settings.SimulatorSpeed = Math.Clamp(r.GetNumber("Simulator speed (×)", _settings.SimulatorSpeed), 0.1, 1000);
        _laser.SetSimulatorSpeed(_settings.SimulatorSpeed);
        _settings.Machine = m.Clone(); // new projects start with this machine
        _settings.Save();
        Surface.ZoomToFit();
        ShowToast("Machine settings saved");
    }

    // ================================================================== view

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (Surface == null || sender is not FrameworkElement { Tag: string tag }) return;
        Surface.Tool = Enum.Parse<DesignTool>(tag);
    }

    private void SelectTool(DesignTool tool)
    {
        foreach (var rb in ToolRail.Children.OfType<RadioButton>())
            if (rb.Tag as string == tool.ToString()) rb.IsChecked = true;
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) { Surface.ZoomToFit(); UpdateZoomText(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { Surface.ZoomBy(1.25); UpdateZoomText(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { Surface.ZoomBy(1 / 1.25); UpdateZoomText(); }

    private void PreviewRaster_Click(object sender, RoutedEventArgs e)
    {
        bool on = sender == PreviewMenu ? PreviewMenu.IsChecked : PreviewCheck.IsChecked == true;
        PreviewMenu.IsChecked = on;
        PreviewCheck.IsChecked = on;
        Surface.PreviewRaster = on;
        Surface.InvalidateVisual();
    }

    private void Snap_Click(object sender, RoutedEventArgs e)
    {
        Surface.SnapToGrid = SnapMenu.IsChecked;
        _settings.SnapToGrid = SnapMenu.IsChecked;
        _settings.Save();
    }

    private void Rulers_Click(object sender, RoutedEventArgs e)
    {
        Surface.ShowRulers = RulersMenu.IsChecked;
        _settings.ShowRulers = RulersMenu.IsChecked;
        _settings.Save();
        Surface.ZoomToFit();
    }

    private void Splash_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowSplash = SplashMenu.IsChecked;
        _settings.Save();
    }

    // ================================================================== help & updates

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(this).ShowDialog();

    private void GitHub_Click(object sender, RoutedEventArgs e) => AboutWindow.OpenUrl(AppInfo.RepositoryUrl);

    private void ReportIssue_Click(object sender, RoutedEventArgs e) =>
        AboutWindow.OpenUrl($"{AppInfo.RepositoryUrl}/issues/new?body={Uri.EscapeDataString("\n\n---\n" + AboutWindow.DetailsText())}");

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userAsked: true);

    private void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdates = AutoUpdateMenu.IsChecked;
        _settings.Save();
    }

    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        var info = await UpdateChecker.CheckAsync();
        if (info == null)
        {
            if (userAsked) ShowToast($"You're up to date – version {AppInfo.Version}", ToastKind.Success);
            return;
        }
        if (!userAsked && info.Version == _settings.SkippedVersion) return;
        _update = info;
        UpdateText.Text = $"A·S·D Lasercraft {info.Version} is available – you have {AppInfo.Version}.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void UpdateView_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) AboutWindow.OpenUrl(_update.DownloadUrl ?? _update.PageUrl);
    }

    private void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) { _settings.SkippedVersion = _update.Version; _settings.Save(); }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateDismiss_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

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
                Key.S when shift => () => SaveAs_Click(this, e),
                Key.S => () => Save_Click(this, e),
                Key.I => () => ImportImage_Click(this, e),
                Key.E => () => ExportGcode_Click(this, e),
                Key.M => () => MaterialLibrary_Click(this, e),
                Key.P => () => Simulate_Click(this, e),
                Key.G => () => AiDesign_Click(this, e),
                Key.K => () => FocusAssistant(),
                _ => null,
            };
            if (a != null) { a(); e.Handled = true; return; }
        }

        // Leave ordinary typing alone in text boxes, combos and grid cells.
        if (Keyboard.FocusedElement is TextBox or ComboBox or DataGridCell) return;

        if (Simulating)
        {
            switch (e.Key)
            {
                case Key.Space: SimPlay_Click(this, e); e.Handled = true; return;
                case Key.Escape: ExitSimulation(); e.Handled = true; return;
                case Key.Home: SimRestart_Click(this, e); e.Handled = true; return;
            }
        }

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
            case Key.OemPlus: case Key.Add: ZoomIn_Click(this, e); break;
            case Key.OemMinus: case Key.Subtract: ZoomOut_Click(this, e); break;
            case Key.V when !ctrl: SelectTool(DesignTool.Select); break;
            case Key.R when !ctrl: SelectTool(DesignTool.Rectangle); break;
            case Key.E when !ctrl: SelectTool(DesignTool.Ellipse); break;
            case Key.G when !ctrl: SelectTool(DesignTool.Polygon); break;
            case Key.L when !ctrl: SelectTool(DesignTool.Line); break;
            case Key.P when !ctrl: SelectTool(DesignTool.Pen); break;
            case Key.T when !ctrl: SelectTool(DesignTool.Text); break;
            case Key.F when !ctrl: ZoomFit_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_smoke && _laser.IsBusy &&
            MessageBox.Show(this, "A job is running. Stop it and quit?", AppInfo.Product, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        ExitSimulation();
        _laser.Dispose();
        if (!_smoke) _settings.Save();
    }
}
