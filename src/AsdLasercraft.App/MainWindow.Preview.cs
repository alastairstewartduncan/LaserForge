using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AsdLasercraft.App.Controls;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Serialization;
using CoreShape = AsdLasercraft.Core.Model.Shape;

namespace AsdLasercraft.App;

// Job building, pre-flight checks and the simulated preview (play the G-code on the canvas before burning).
public partial class MainWindow
{
    private static readonly double[] SimSpeeds = { 1, 5, 20, 100, 500 };
    private readonly Stopwatch _simClock = new();
    private bool _simPlaying;
    private bool _simUpdatingSlider;
    private double _simLastTick;
    private int _simBuildVersion;

    // ================================================================== job building + checks

    /// <summary>
    /// Generates the job (with every move recorded, so overscan can be checked) off the UI thread and runs the
    /// pre-flight checks. Returns null if there is nothing to burn.
    /// </summary>
    private async Task<(GcodeJob Job, List<PreflightIssue> Issues)?> PrepareJobAsync()
    {
        var only = SelectedOnlyCheck.IsChecked == true && Surface.Selection.Count > 0 ? Selected() : null;
        var onlyIds = only?.Select(s => s.Id).ToHashSet();
        var snapshot = ProjectSerializer.DeepClone(_project);
        Cursor = Cursors.Wait;
        try
        {
            return await Task.Run(() =>
            {
                var shapes = onlyIds == null ? null : snapshot.Shapes.Where(s => onlyIds.Contains(s.Id)).ToList();
                var job = GcodeGenerator.Generate(snapshot, new GcodeOptions { OnlyThese = shapes, RecordMoves = true });
                var issues = Preflight.Check(snapshot, job, onlyIds);
                return ((GcodeJob, List<PreflightIssue>)?)(job, issues);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't generate the job:\n{ex.Message}", "Job", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally { Cursor = null; }
    }

    private string Subtitle(GcodeJob job)
    {
        int layers = _project.Layers.Count(l => l.Output && _project.ShapesOn(l).Any());
        string origin = _project.Job.Origin == JobOrigin.Absolute
            ? "absolute coordinates"
            : $"{JobSettings.Describe(_project.Job.Origin).ToLowerInvariant()}, {JobSettings.Describe(_project.Job.Anchor)}";
        return $"{layers} layer{(layers == 1 ? "" : "s")} · {job.Lines.Count:N0} lines · {origin}";
    }

    // ================================================================== simulated preview

    private bool Simulating => Surface?.Simulation != null;

    private async void Simulate_Click(object sender, RoutedEventArgs e)
    {
        if (Simulating) ExitSimulation();
        else await EnterSimulationAsync();
        SyncSimulateToggles();
    }

    private void SyncSimulateToggles()
    {
        SimulateToggle.IsChecked = Simulating;
        SimulateMenu.IsChecked = Simulating;
    }

    private async Task EnterSimulationAsync()
    {
        int version = ++_simBuildVersion;
        var prepared = await PrepareJobAsync();
        if (version != _simBuildVersion) return;
        if (prepared is not { } p || p.Job.Moves.Count == 0)
        {
            ShowToast("Nothing to preview yet.", ToastKind.Info);
            SyncSimulateToggles();
            return;
        }
        StartSimulation(p.Job, autoPlay: true);
    }

    /// <summary>Shows the preview for an already-generated job (used directly by the smoke test).</summary>
    internal void StartSimulation(GcodeJob job, bool autoPlay)
    {
        var rasterLayers = _project.Layers.Where(l => l.Output && l.Mode != LayerMode.Line && _project.ShapesOn(l).Any()).ToList();
        double burnWidth = rasterLayers.Count > 0 ? rasterLayers.Min(l => l.LineIntervalMm) : 0.15;
        var sim = new SimulationData(job, burnWidth);
        Surface.Simulation = sim;
        Surface.SimulationTime = 0;
        Surface.ShowTravel = SimTravelToggle.IsChecked == true;
        _simUpdatingSlider = true;
        SimSlider.Maximum = Math.Max(0.001, sim.TotalSeconds);
        SimSlider.Value = 0;
        _simUpdatingSlider = false;
        SimInfoText.Text = $"{job.Bounds.Width:0.#} × {job.Bounds.Height:0.#} MM  ·  {job.CutDistanceMm / 1000:0.0} M BURNED  ·  {job.Moves.Count:N0} MOVES";
        SimBar.Visibility = Visibility.Visible;
        UpdateSimTime();
        SyncSimulateToggles();
        Surface.InvalidateVisual();
        if (autoPlay) PlaySimulation();
    }

    private void ExitSimulation()
    {
        if (Surface == null || Surface.Simulation == null) return;
        PauseSimulation();
        Surface.Simulation = null;
        SimBar.Visibility = Visibility.Collapsed;
        SyncSimulateToggles();
        Surface.InvalidateVisual();
    }

    private void PlaySimulation()
    {
        if (Surface.Simulation is not { } sim) return;
        if (Surface.SimulationTime >= sim.TotalSeconds - 1e-6) Surface.SimulationTime = 0; // replay from the start
        _simPlaying = true;
        _simClock.Restart();
        _simLastTick = 0;
        SetGlyph(SimPlayGlyph, "I.Pause", "");
        CompositionTarget.Rendering -= OnSimFrame;
        CompositionTarget.Rendering += OnSimFrame;
    }

    private void PauseSimulation()
    {
        _simPlaying = false;
        CompositionTarget.Rendering -= OnSimFrame;
        SetGlyph(SimPlayGlyph, "I.Play", "");
    }

    private void OnSimFrame(object? sender, EventArgs e)
    {
        if (!_simPlaying || Surface.Simulation is not { } sim) { PauseSimulation(); return; }
        double now = _simClock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - _simLastTick);
        _simLastTick = now;
        double speed = SimSpeeds[Math.Clamp(SimSpeedCombo.SelectedIndex, 0, SimSpeeds.Length - 1)];
        Surface.SimulationTime = Math.Min(sim.TotalSeconds, Surface.SimulationTime + dt * speed);
        UpdateSimTime();
        Surface.InvalidateVisual();
        if (Surface.SimulationTime >= sim.TotalSeconds) PauseSimulation();
    }

    private void UpdateSimTime()
    {
        if (Surface.Simulation is not { } sim) return;
        _simUpdatingSlider = true;
        SimSlider.Value = Surface.SimulationTime;
        _simUpdatingSlider = false;
        SimTimeText.Text = $"{FormatTime(TimeSpan.FromSeconds(Surface.SimulationTime))} / {FormatTime(TimeSpan.FromSeconds(sim.TotalSeconds))}";
    }

    private void SimPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_simPlaying) PauseSimulation(); else PlaySimulation();
    }

    private void SimRestart_Click(object sender, RoutedEventArgs e)
    {
        Surface.SimulationTime = 0;
        UpdateSimTime();
        Surface.InvalidateVisual();
    }

    private void SimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_simUpdatingSlider || Surface?.Simulation == null) return;
        Surface.SimulationTime = e.NewValue;
        UpdateSimTime();
        Surface.InvalidateVisual();
    }

    private void SimSpeed_Changed(object sender, SelectionChangedEventArgs e) { /* read on every frame */ }

    private void SimTravel_Click(object sender, RoutedEventArgs e)
    {
        if (Surface == null) return;
        Surface.ShowTravel = SimTravelToggle.IsChecked == true;
        Surface.InvalidateVisual();
    }

    private void SimClose_Click(object sender, RoutedEventArgs e) => ExitSimulation();
}
