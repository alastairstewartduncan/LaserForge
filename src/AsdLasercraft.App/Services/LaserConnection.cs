using System.IO;
using System.Windows.Threading;
using AsdLasercraft.Core.Grbl;

namespace AsdLasercraft.App.Services;

/// <summary>
/// Owns the link to the laser: a USB serial port or the built-in simulator, the GRBL streamer on top of it,
/// and the status polling timer. Every event is raised on the UI thread.
/// </summary>
public sealed class LaserConnection : IDisposable
{
    public const string SimulatorPort = "Simulator (no laser)";

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _statusTimer;
    private SerialTransport? _serial;
    private GrblSimulator? _simulator;
    private int _progressThrottle;

    public LaserConnection(Dispatcher ui)
    {
        _ui = ui;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(200) };
        _statusTimer.Tick += (_, _) => Streamer?.RequestStatus();
    }

    public GrblStreamer? Streamer { get; private set; }
    public bool IsConnected => Streamer != null;
    public bool IsSimulator => _simulator != null;
    public bool IsBusy => Streamer?.IsBusy == true;
    public string? PortName { get; private set; }

    public event Action<int, int>? ProgressChanged;
    public event Action<StreamState>? StateChanged;
    public event Action<GrblStatus>? StatusReceived;
    public event Action<string>? Log;
    public event Action<string, string>? ErrorReported;
    public event Action? ConnectionChanged;

    public static string[] AvailablePorts() =>
        SerialTransport.AvailablePorts().Append(SimulatorPort).ToArray();

    /// <summary>Opens the port (or starts the simulator). Throws if the port can't be opened.</summary>
    public void Connect(string port, int baud, int rxBufferSize, double simulatorSpeed)
    {
        if (IsConnected) Disconnect();
        ILineTransport transport;
        if (port == SimulatorPort)
        {
            _simulator = new GrblSimulator(rxBufferSize) { SpeedFactor = Math.Max(0.1, simulatorSpeed) };
            transport = _simulator;
        }
        else
        {
            _serial = new SerialTransport(port, baud);
            try { _serial.Open(); }
            catch
            {
                _serial.Dispose();
                _serial = null;
                throw;
            }
            transport = _serial;
        }

        var s = new GrblStreamer(transport, rxBufferSize);
        // Raster jobs acknowledge thousands of lines a second; only forward every 25th progress tick.
        s.ProgressChanged += (ack, total) =>
        {
            if (ack >= total || Interlocked.Increment(ref _progressThrottle) % 25 == 0)
                _ui.InvokeAsync(() => ProgressChanged?.Invoke(ack, total));
        };
        s.StateChanged += st => _ui.InvokeAsync(() => StateChanged?.Invoke(st));
        s.ErrorReported += (line, err) => _ui.InvokeAsync(() => ErrorReported?.Invoke(line, err));
        s.MessageReceived += msg => _ui.InvokeAsync(() => Log?.Invoke(msg));
        s.StatusReceived += st => _ui.InvokeAsync(() => StatusReceived?.Invoke(st));
        s.Incident += reason => SaveDiagnostics(s, reason);
        if (_serial != null) _serial.Faulted += msg => s.Abort(msg);
        Streamer = s;
        PortName = port;
        _simulator?.StartRealtime();
        _statusTimer.Start();
        ConnectionChanged?.Invoke();
    }

    public void Disconnect()
    {
        _statusTimer.Stop();
        if (Streamer?.IsBusy == true) Streamer.Stop();
        Streamer = null;
        _serial?.Dispose();
        _serial = null;
        _simulator?.Dispose();
        _simulator = null;
        PortName = null;
        ConnectionChanged?.Invoke();
    }

    /// <summary>Folder where stall diagnostics are written.</summary>
    public static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD Lasercraft", "logs");

    // Writes the streamer's recent traffic to a file whenever it had to recover or stop a job by itself,
    // so a stall can be diagnosed from the file instead of guessed at. Runs off the serial threads.
    private long _lastDiagnosticsMs = long.MinValue / 2;

    private void SaveDiagnostics(GrblStreamer s, string reason)
    {
        // A flaky link can recover every few seconds; one file per 30 s is plenty. Stopped jobs always get one.
        long now = Environment.TickCount64;
        if (s.State != StreamState.Faulted && now - Interlocked.Read(ref _lastDiagnosticsMs) < 30_000) return;
        Interlocked.Exchange(ref _lastDiagnosticsMs, now);
        string text = s.Diagnostics(reason + $" (transport queue: {_serial?.QueuedItems ?? 0} item(s))");
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                foreach (var old in new DirectoryInfo(LogFolder).GetFiles("stall-*.log")
                             .OrderByDescending(f => f.Name).Skip(20)) old.Delete();
                string path = Path.Combine(LogFolder, $"stall-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                File.WriteAllText(path, $"{AsdLasercraft.Core.AppInfo.ShortName} {AsdLasercraft.Core.AppInfo.Version}, port {PortName}\n" + text);
                _ui.InvokeAsync(() => Log?.Invoke($"Diagnostic log saved: {path}"));
            }
            catch (Exception ex)
            {
                _ui.InvokeAsync(() => Log?.Invoke("Couldn't save the diagnostic log: " + ex.Message));
            }
        });
    }

    /// <summary>Simulator speed multiplier (only affects the simulator).</summary>
    public void SetSimulatorSpeed(double factor)
    {
        if (_simulator != null) _simulator.SpeedFactor = Math.Max(0.1, factor);
    }

    public void Dispose() => Disconnect();
}
