using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Grbl;
using AsdLasercraft.Core.Model;
using Vec2 = AsdLasercraft.Core.Geometry.Vec2;

namespace AsdLasercraft.App;

// Laser tab: connection, machine control, where the job starts (origin + anchor), and running jobs.
public partial class MainWindow
{
    private LaserConnection _laser = null!;
    private JobPlacement? _runningPlacement;   // placement of the job/frame currently streaming
    private double _runningEstimate;           // seconds, for the remaining-time readout
    private DateTime _jobStarted;
    private bool _runningIsFrame;

    private void InitLaser()
    {
        _laser = new LaserConnection(Dispatcher);
        _laser.ProgressChanged += OnProgress;
        _laser.StateChanged += OnStreamState;
        _laser.ErrorReported += (line, err) => Log($"!! {err}   (line: {line})");
        _laser.Log += Log;
        _laser.StatusReceived += OnStatus;
        _laser.ConnectionChanged += RefreshConnectionUi;
        RefreshConnectionUi();
    }

    // ================================================================== connection

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var ports = LaserConnection.AvailablePorts();
        PortCombo.ItemsSource = ports;
        if (_settings.LastPort != null && ports.Contains(_settings.LastPort)) PortCombo.SelectedItem = _settings.LastPort;
        else if (ports.Length > 1) PortCombo.SelectedIndex = ports.Length - 2; // highest COM port (USB adapters), before the simulator
        else PortCombo.SelectedIndex = ports.Length - 1;                        // only the simulator
        BaudCombo.Text = _settings.LastBaud.ToString(CultureInfo.InvariantCulture);
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_laser.IsConnected) { Disconnect(); return; }
        if (PortCombo.SelectedItem is not string port) { ShowToast("Choose a port first.", ToastKind.Info); return; }
        if (!int.TryParse(BaudCombo.Text, out int baud)) baud = 115200;
        try
        {
            _laser.Connect(port, baud, _project.Machine.RxBufferSize, _settings.SimulatorSpeed);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {port}:\n{ex.Message}", "Connect", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _settings.LastPort = port;
        _settings.LastBaud = baud;
        _settings.Save();
        Log(_laser.IsSimulator ? $"Connected to the built-in simulator ({_settings.SimulatorSpeed:0.#}× speed)" : $"Connected to {port} @ {baud}");
        ShowToast(_laser.IsSimulator ? "Simulator connected – no laser needed" : $"Connected to {port}", ToastKind.Success);
    }

    private void Disconnect()
    {
        if (_laser.IsBusy &&
            MessageBox.Show(this, "A job is running. Stop it and disconnect?", "Disconnect", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _laser.Disconnect();
        Surface.LaserPosition = null;
        Surface.LaserFiring = false;
        Surface.InvalidateVisual();
        OnStreamState(StreamState.Idle);
        Log("Disconnected");
    }

    private void RefreshConnectionUi()
    {
        bool on = _laser.IsConnected;
        SetGlyph(ConnectGlyph, on ? "I.Unplug" : "I.Plug", on ? "Disconnect" : "Connect");
        var dot = (Brush)FindResource(on ? (_laser.IsSimulator ? "Brush.Info" : "Brush.Success") : "Brush.TextDim");
        ConnDot.Fill = dot;
        ConnDotLarge.Fill = dot;
        ConnStateText.Text = on ? (_laser.IsSimulator ? "Simulator (no laser)" : $"Connected · {_laser.PortName}") : "Not connected";
        if (!on) MachineText.Text = "Not connected";
        PortCombo.IsEnabled = BaudCombo.IsEnabled = !on;
    }

    private void OnStatus(GrblStatus st)
    {
        var pos = st.WorkPos ?? st.MachinePos;
        MachineText.Text = pos is { } p
            ? $"{st.State}  X{p.X:0.00} Y{p.Y:0.00}" + (_laser.IsSimulator ? "  · sim" : "")
            : st.State;
        if (pos is not { } wp) return;

        var projectPos = ToProject(wp);
        bool firing = st.State.StartsWith("Run", StringComparison.Ordinal) && st.Spindle > 0 && !_runningIsFrame;
        Surface.LaserPosition = projectPos;
        Surface.LaserFiring = firing;
        if (_laser.IsBusy) Surface.AddTrailPoint(projectPos, firing);
        Surface.InvalidateVisual();
    }

    /// <summary>Converts a reported work position back to project space for the crosshair.</summary>
    private Vec2 ToProject(Vec2 work)
    {
        var m = _project.Machine;
        if (_laser.IsBusy && _runningPlacement is { IsRelative: true } pl)
            return new Vec2(pl.Anchor.X + work.X, pl.Anchor.Y + (m.FlipY ? -work.Y : work.Y));
        return m.FlipY ? new Vec2(work.X, m.BedHeightMm - work.Y) : work;
    }

    private void OnProgress(int ack, int total)
    {
        JobProgress.Maximum = Math.Max(1, total);
        JobProgress.Value = ack;
        double frac = total == 0 ? 0 : (double)ack / total;
        JobProgressText.Text = $"{ack:N0} / {total:N0} lines  ({frac * 100:0}%)";
        if (_laser.IsBusy && _runningEstimate > 0)
        {
            var elapsed = DateTime.Now - _jobStarted;
            var left = TimeSpan.FromSeconds(Math.Max(0, _runningEstimate * (1 - frac)));
            JobEtaText.Text = $"{FormatTime(elapsed)} · ~{FormatTime(left)} left";
        }
    }

    private void OnStreamState(StreamState s)
    {
        bool busy = s is StreamState.Running or StreamState.Paused;
        StartButton.IsEnabled = !busy;
        PauseButton.IsEnabled = busy;
        SetGlyph(PauseGlyph, s == StreamState.Paused ? "I.Play" : "I.Pause", s == StreamState.Paused ? "Resume" : "Pause");
        switch (s)
        {
            case StreamState.Completed:
                JobProgressText.Text = _runningIsFrame ? "Frame done" : "Done ✓";
                if (!_runningIsFrame)
                    ShowToast($"Job complete in {FormatTime(DateTime.Now - _jobStarted)}", ToastKind.Success);
                break;
            case StreamState.Stopped:
                JobProgressText.Text = "Stopped";
                break;
            case StreamState.Faulted:
                JobProgressText.Text = "Stopped on error – see console";
                ShowToast("Job stopped on an error – see the console", ToastKind.Error);
                break;
        }
        if (!busy)
        {
            Surface.LaserFiring = false;
            JobEtaText.Text = "";
        }
        if (s != StreamState.Idle) Log($"-- {(_runningIsFrame ? "frame" : "job")} {s.ToString().ToLowerInvariant()}");
    }

    private bool RequireConnection()
    {
        if (_laser.IsConnected) return true;
        RightTabs.SelectedIndex = 1;
        ShowToast("Connect to the laser first – or pick the Simulator to try it out.", ToastKind.Info);
        return false;
    }

    private void Send(string command)
    {
        if (!RequireConnection()) return;
        if (_laser.Streamer!.SendCommand(command)) Log("> " + command);
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
        if (!_laser.Streamer!.Jog(parts[0] * step, parts[1] * step, feed)) Log("(busy – jog ignored)");
    }

    private void JogCancel_Click(object sender, RoutedEventArgs e) => _laser.Streamer?.CancelJog();

    // ================================================================== job origin & anchor

    private void RefreshOriginUi()
    {
        _updatingUi = true;
        try
        {
            var job = _project.Job;
            OriginCombo.SelectedIndex = (int)job.Origin;
            foreach (var rb in AnchorGrid.Children.OfType<RadioButton>())
                rb.IsChecked = rb.Tag as string == job.Anchor.ToString();
            AnchorGrid.IsEnabled = job.Origin != JobOrigin.Absolute;
            OriginHint.Text = job.Origin switch
            {
                JobOrigin.UserOrigin => $"The job's {JobSettings.Describe(job.Anchor)} goes to the origin you set with “Set origin here”.",
                JobOrigin.CurrentPosition => $"The job's {JobSettings.Describe(job.Anchor)} starts wherever the head is. Jog there, frame, then start.",
                _ => "Bed positions are machine coordinates. Home the laser first.",
            };
            OriginChipText.Text = job.Origin switch
            {
                JobOrigin.UserOrigin => $"User origin · {JobSettings.Describe(job.Anchor)}",
                JobOrigin.CurrentPosition => $"Head position · {JobSettings.Describe(job.Anchor)}",
                _ => "Absolute",
            };
        }
        finally { _updatingUi = false; }
        UpdateAnchorMarker();
    }

    private void UpdateAnchorMarker()
    {
        if (Surface == null) return;
        var job = _project.Job;
        if (job.Origin == JobOrigin.Absolute) { Surface.JobAnchor = null; return; }
        var bounds = SelectedOnlyCheck?.IsChecked == true && Surface.Selection.Count > 0 ? Surface.SelectionBounds() : _project.OutputBounds();
        Surface.JobAnchor = bounds.IsEmpty ? null : JobSettings.AnchorPoint(bounds, job.Anchor);
        Surface.InvalidateVisual();
    }

    private void Origin_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || OriginCombo.SelectedIndex < 0) return;
        var origin = (JobOrigin)OriginCombo.SelectedIndex;
        if (origin == _project.Job.Origin) return;
        Edit(() => _project.Job.Origin = origin);
        RefreshOriginUi();
    }

    private void Anchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<JobAnchor>(tag, out var anchor)) return;
        if (anchor == _project.Job.Anchor) return;
        Edit(() => _project.Job.Anchor = anchor);
        RefreshOriginUi();
    }

    // ================================================================== frame / start / pause / stop

    private void Frame_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || _laser.IsBusy) return;
        var bounds = SelectedOnlyCheck.IsChecked == true && Surface.Selection.Count > 0 ? Surface.SelectionBounds() : _project.OutputBounds();
        double power = TryNum(FramePowerBox.Text, out var p) ? Math.Clamp(p, 0, 5) : 0; // capped: framing must never mark
        double speed = TryNum(FrameSpeedBox.Text, out var s) ? s : 3000;
        var frame = GcodeGenerator.GenerateFrame(_project, power, speed, bounds);
        if (frame.Bounds.IsEmpty) { ShowToast("Nothing to frame yet.", ToastKind.Info); return; }
        Log($"Framing {frame.Bounds.Width:0.#} × {frame.Bounds.Height:0.#} mm at {power:0.#}% power");
        BeginStreaming(frame, isFrame: true);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || _laser.IsBusy) return;
        var prepared = await PrepareJobAsync();
        if (prepared is not { } p) return;
        var confirm = new JobConfirmWindow(this, p.Job, p.Issues, Subtitle(p.Job));
        if (confirm.ShowDialog() != true) return;
        if (confirm.Result == JobConfirmResult.Simulate) { await EnterSimulationAsync(); return; }
        if (!_laser.IsConnected || _laser.IsBusy) return;
        Log($"Starting job: {p.Job.Lines.Count:N0} lines");
        BeginStreaming(p.Job, isFrame: false);
    }

    private void BeginStreaming(GcodeJob job, bool isFrame)
    {
        ExitSimulation();
        _runningPlacement = job.Placement;
        _runningEstimate = job.EstimatedSeconds;
        _runningIsFrame = isFrame;
        _jobStarted = DateTime.Now;
        if (!isFrame) Surface.ClearTrail();
        JobProgress.Value = 0;
        JobEtaText.Text = "";
        _laser.Streamer!.Start(job.Lines);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        var s = _laser.Streamer;
        if (s == null) return;
        if (s.State == StreamState.Paused) s.Resume();
        else s.Pause();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _laser.Streamer?.Stop();
        Surface.LaserFiring = false;
        Log("STOP sent (feed hold + soft reset). You may need to Unlock ($X) or Home afterwards.");
    }

    // ================================================================== console

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
        // Explain GRBL alarms and errors in plain English, right where they appear (works offline).
        if (AsdLasercraft.Core.Grbl.GrblCodes.Explain(text) is { } info)
            ConsoleList.Items.Add($"          ↳ {info.Meaning} {info.Fix}");
        while (ConsoleList.Items.Count > 1000) ConsoleList.Items.RemoveAt(0);
        ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
    }
}
