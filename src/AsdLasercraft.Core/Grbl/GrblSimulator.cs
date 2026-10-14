using System.Diagnostics;
using System.Globalization;
using System.Text;
using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Grbl;

/// <summary>
/// A software stand-in for a GRBL 1.1 laser controller. It speaks the same serial protocol as the real thing:
/// buffers incoming bytes (and records an overflow if the sender ever exceeds the RX buffer), answers "ok" or
/// "error:N" per line, reports status for '?', obeys feed hold / resume / soft reset, and tracks the head position
/// including work offsets (G10 L20, G92).
///
/// Two ways to drive it:
/// <list type="bullet">
/// <item><see cref="ProcessAll"/> — tests: processes queued lines instantly on the caller's thread.</item>
/// <item><see cref="StartRealtime"/> — the app's "Simulator" connection: a background thread executes moves at
/// (a multiple of) their real speed, so the preview crosshair moves like a real laser head.</item>
/// </list>
/// Responses are never raised while the simulator holds its own lock, so it can't deadlock with the streamer.
/// </summary>
public sealed class GrblSimulator : ILineTransport, IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<string> _outbox = new();
    private readonly StringBuilder _partial = new();
    private int _bufferedBytes;

    private Vec2 _mpos;            // machine position
    private Vec2 _g54;             // work coordinate offset set by G10 L20 P1
    private Vec2 _g92;             // temporary offset set by G92
    private double _feed = 1000, _s;
    private bool _laserOn, _hold, _absolute = true;
    private string _motion = "G0";

    // realtime execution
    private Thread? _thread;
    private volatile bool _running;
    private Vec2 _moveFrom, _moveTo;
    private long _moveStartTicks, _moveDurationTicks;
    private bool _moving;
    private long _holdAt;          // timestamp when feed hold began mid-move (0 = not holding)

    public GrblSimulator(int rxBufferSize = 128) => RxBufferSize = rxBufferSize;

    public event Action<string>? LineReceived;

    public int RxBufferSize { get; }

    /// <summary>True if the sender ever pushed more bytes than the RX buffer holds (a real controller would lose data).</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Highest number of bytes ever waiting in the RX buffer.</summary>
    public int MaxBufferedBytes { get; private set; }

    /// <summary>Every complete line received, in order (real-time bytes excluded).</summary>
    public List<string> ReceivedLines { get; } = new();

    /// <summary>Real-time characters received ('?', '!', '~', 0x18, 0x85…).</summary>
    public List<char> RealtimeReceived { get; } = new();

    /// <summary>Optional hook: return an "error:N" string to reject a line, or null to accept it.</summary>
    public Func<string, string?>? Responder { get; set; }

    /// <summary>Realtime mode only: 1 = real speed, 10 = ten times faster.</summary>
    public double SpeedFactor { get; set; } = 1;

    public Vec2 MachinePosition { get { lock (_lock) return CurrentPositionLocked(); } }
    public Vec2 WorkOffset { get { lock (_lock) return _g54 + _g92; } }
    public bool LaserOn { get { lock (_lock) return _laserOn && _s > 0; } }
    public double SpindleS { get { lock (_lock) return _s; } }

    public void Write(string text)
    {
        lock (_lock)
        {
            foreach (char c in text)
            {
                switch (c)
                {
                    case '?':
                        RealtimeReceived.Add(c);
                        _outbox.Enqueue(StatusLocked());
                        continue;
                    case '!':
                        RealtimeReceived.Add(c);
                        if (!_hold && _moving) _holdAt = Stopwatch.GetTimestamp();
                        _hold = true;
                        continue;
                    case '~':
                        RealtimeReceived.Add(c);
                        if (_hold && _holdAt != 0) _moveStartTicks += Stopwatch.GetTimestamp() - _holdAt; // resume where we froze
                        _holdAt = 0;
                        _hold = false;
                        continue;
                    case '\x18':
                        RealtimeReceived.Add(c);
                        ResetLocked();
                        continue;
                    case '\x85':
                        RealtimeReceived.Add(c);
                        continue;
                    case '\r':
                        continue;
                    case '\n':
                    {
                        var line = _partial.ToString();
                        _partial.Clear();
                        _bufferedBytes += line.Length + 1;
                        MaxBufferedBytes = Math.Max(MaxBufferedBytes, _bufferedBytes);
                        if (_bufferedBytes > RxBufferSize) Overflowed = true;
                        _pending.Enqueue(line);
                        continue;
                    }
                    default:
                        _partial.Append(c);
                        if (_bufferedBytes + _partial.Length > RxBufferSize) Overflowed = true;
                        continue;
                }
            }
        }
        if (!_running) FlushOutbox(); // in realtime mode the worker thread delivers responses
    }

    /// <summary>Processes every queued line immediately (test mode). Lines sent in reply to responses are processed too.</summary>
    public void ProcessAll(int maxLines = int.MaxValue)
    {
        int done = 0;
        while (done < maxLines)
        {
            string? line;
            lock (_lock)
            {
                if (_hold || !_pending.TryDequeue(out line)) break;
                _bufferedBytes -= line.Length + 1;
                ExecuteLocked(line, instant: true);
            }
            done++;
            FlushOutbox();
        }
        FlushOutbox();
    }

    /// <summary>Starts executing moves on a background thread at <see cref="SpeedFactor"/> × real speed.</summary>
    public void StartRealtime()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(RealtimeLoop) { IsBackground = true, Name = "GRBL simulator" };
        _thread.Start();
        Emit("Grbl 1.1h ['$' for help]");
        Emit("[MSG:A·S·D Lasercraft simulator – no laser connected]");
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
    }

    private void RealtimeLoop()
    {
        while (_running)
        {
            FlushOutbox();
            string? line = null;
            long waitTicks = 0;
            lock (_lock)
            {
                if (_moving)
                {
                    long elapsed = Now() - _moveStartTicks;
                    if (elapsed >= _moveDurationTicks) { _moving = false; _mpos = _moveTo; }
                    else waitTicks = _moveDurationTicks - elapsed;
                }
                if (!_moving && !_hold && _pending.TryDequeue(out line))
                {
                    _bufferedBytes -= line.Length + 1;
                    ExecuteLocked(line, instant: false);
                }
            }
            FlushOutbox();
            if (line == null)
            {
                // Idle or mid-move: sleep a little (Windows timers are ~1–15 ms, so don't try to be exact).
                double ms = waitTicks > 0 ? waitTicks * 1000.0 / Stopwatch.Frequency : 5;
                Thread.Sleep(Math.Clamp((int)ms, 1, 10));
            }
        }
    }

    // ------------------------------------------------------------------ command execution (caller holds _lock)

    private void ExecuteLocked(string raw, bool instant)
    {
        var line = raw.Trim();
        ReceivedLines.Add(line);
        if (line.Length == 0) { _outbox.Enqueue("ok"); return; }

        var injected = Responder?.Invoke(line);
        if (injected != null) { _outbox.Enqueue(injected); return; }

        if (line.StartsWith('$'))
        {
            ExecuteDollarLocked(line, instant);
            return;
        }

        var words = Parse(line);
        if (words.TryGetValue('G', out var gs))
        {
            foreach (var g in gs)
            {
                switch (Fmt(g))
                {
                    case "0": _motion = "G0"; break;
                    case "1": _motion = "G1"; break;
                    case "90": _absolute = true; break;
                    case "91": _absolute = false; break;
                    case "92":
                        // G92 X0 Y0: the current position becomes the given work coordinates.
                        var cur = CurrentPositionLocked();
                        double gx = First(words, 'X') ?? (cur - _g54 - _g92).X;
                        double gy = First(words, 'Y') ?? (cur - _g54 - _g92).Y;
                        _g92 = new Vec2(cur.X - _g54.X - gx, cur.Y - _g54.Y - gy);
                        _outbox.Enqueue("ok");
                        return;
                    case "92.1": _g92 = Vec2.Zero; break;
                    case "10":
                        // G10 L20 P1 X0 Y0: set G54 so the current position reads as the given coordinates.
                        if (First(words, 'L') == 20)
                        {
                            var p = CurrentPositionLocked();
                            double x = First(words, 'X') ?? (p - _g54 - _g92).X;
                            double y = First(words, 'Y') ?? (p - _g54 - _g92).Y;
                            _g54 = new Vec2(p.X - _g92.X - x, p.Y - _g92.Y - y);
                        }
                        _outbox.Enqueue("ok");
                        return;
                }
            }
        }
        if (words.TryGetValue('M', out var ms))
        {
            foreach (var mcode in ms)
            {
                if (mcode is 3 or 4) _laserOn = true;
                else if (mcode is 5 or 2 or 30) _laserOn = false;
            }
        }
        if (First(words, 'F') is { } f && f > 0) _feed = f;
        if (First(words, 'S') is { } s) _s = s;

        double? wx = First(words, 'X'), wy = First(words, 'Y');
        if (wx != null || wy != null)
        {
            var work = CurrentPositionLocked() - _g54 - _g92;
            var target = _absolute
                ? new Vec2(wx ?? work.X, wy ?? work.Y)
                : new Vec2(work.X + (wx ?? 0), work.Y + (wy ?? 0));
            MoveToLocked(target + _g54 + _g92, _motion == "G0" ? 6000 : _feed, instant);
        }
        _outbox.Enqueue("ok");
    }

    private void ExecuteDollarLocked(string line, bool instant)
    {
        if (line == "$H")
        {
            MoveToLocked(Vec2.Zero, 3000, instant);
            _outbox.Enqueue("ok");
        }
        else if (line == "$X")
        {
            _outbox.Enqueue("[MSG:Caution: Unlocked]");
            _outbox.Enqueue("ok");
        }
        else if (line == "$$")
        {
            foreach (var setting in new[] { "$30=1000", "$31=0", "$32=1", "$110=6000", "$111=6000", "$120=1500", "$121=1500", "$130=400", "$131=400" })
                _outbox.Enqueue(setting);
            _outbox.Enqueue("ok");
        }
        else if (line.StartsWith("$J=", StringComparison.Ordinal))
        {
            var words = Parse(line[3..]);
            bool relative = words.TryGetValue('G', out var g) && g.Contains(91);
            var work = CurrentPositionLocked() - _g54 - _g92;
            double? x = First(words, 'X'), y = First(words, 'Y');
            var target = relative ? new Vec2(work.X + (x ?? 0), work.Y + (y ?? 0)) : new Vec2(x ?? work.X, y ?? work.Y);
            MoveToLocked(target + _g54 + _g92, First(words, 'F') ?? 3000, instant);
            _outbox.Enqueue("ok");
        }
        else _outbox.Enqueue("ok");
    }

    private void MoveToLocked(Vec2 target, double feedMmMin, bool instant)
    {
        var from = CurrentPositionLocked();
        double dist = Vec2.Distance(from, target);
        if (instant || dist < 1e-9 || feedMmMin <= 0)
        {
            _mpos = target;
            _moving = false;
            return;
        }
        double seconds = dist / feedMmMin * 60.0 / Math.Max(0.01, SpeedFactor);
        _moveFrom = from;
        _moveTo = target;
        _moveStartTicks = Stopwatch.GetTimestamp();
        _moveDurationTicks = Math.Max(1, (long)(seconds * Stopwatch.Frequency));
        _moving = true;
    }

    private Vec2 CurrentPositionLocked()
    {
        if (!_moving) return _mpos;
        double t = Math.Clamp((Now() - _moveStartTicks) / (double)_moveDurationTicks, 0, 1);
        return Vec2.Lerp(_moveFrom, _moveTo, t);
    }

    // While feed hold is active mid-move, time stands still for the move.
    private long Now() => _holdAt != 0 ? _holdAt : Stopwatch.GetTimestamp();

    private string StatusLocked()
    {
        string state = _hold ? "Hold:0" : (_moving || _pending.Count > 0) ? "Run" : "Idle";
        var p = CurrentPositionLocked();
        var wco = _g54 + _g92;
        double s = _laserOn && (_moving || _pending.Count > 0) ? _s : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"<{state}|MPos:{p.X:0.000},{p.Y:0.000},0.000|FS:{(_moving ? _feed : 0):0},{s:0}|WCO:{wco.X:0.000},{wco.Y:0.000},0.000>");
    }

    private void ResetLocked()
    {
        if (_moving) { _mpos = CurrentPositionLocked(); _moving = false; }
        _holdAt = 0;
        _pending.Clear();
        _partial.Clear();
        _bufferedBytes = 0;
        _hold = false;
        _laserOn = false;
        _s = 0;
        _absolute = true;
        _motion = "G0";
        _outbox.Enqueue("Grbl 1.1h ['$' for help]");
    }

    private void Emit(string line)
    {
        lock (_lock) _outbox.Enqueue(line);
        FlushOutbox();
    }

    private void FlushOutbox()
    {
        while (true)
        {
            string? line;
            lock (_lock)
            {
                if (!_outbox.TryDequeue(out line)) return;
            }
            LineReceived?.Invoke(line);
        }
    }

    // ------------------------------------------------------------------ parsing

    private static Dictionary<char, List<double>> Parse(string line)
    {
        var words = new Dictionary<char, List<double>>();
        int i = 0;
        while (i < line.Length)
        {
            char c = char.ToUpperInvariant(line[i]);
            if (c is >= 'A' and <= 'Z')
            {
                int j = i + 1;
                while (j < line.Length && (char.IsDigit(line[j]) || line[j] is '.' or '-' or '+')) j++;
                if (double.TryParse(line.AsSpan(i + 1, j - i - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    if (!words.TryGetValue(c, out var list)) words[c] = list = new List<double>();
                    list.Add(v);
                }
                i = j;
            }
            else i++;
        }
        return words;
    }

    private static double? First(Dictionary<char, List<double>> words, char c) =>
        words.TryGetValue(c, out var l) && l.Count > 0 ? l[0] : null;

    private static string Fmt(double g) => g.ToString("0.#", CultureInfo.InvariantCulture);
}
