using System.Text;

namespace AsdLasercraft.Core.Grbl;

/// <summary>Abstraction over the serial link so the streamer can be unit-tested without hardware.</summary>
public interface ILineTransport
{
    /// <summary>Writes raw text (no newline added). Used for real-time commands like '?', '!', '~' and 0x18.</summary>
    void Write(string text);

    /// <summary>Raised for every complete line received from the controller (without the newline).</summary>
    event Action<string>? LineReceived;
}

public enum StreamState { Idle, Running, Paused, Completed, Stopped, Faulted }

/// <summary>
/// Streams G-code to GRBL using the character-counting protocol: keeps GRBL's serial RX buffer
/// (127 usable bytes by default) as full as possible without overflowing it, which is what makes
/// raster engraving smooth. Each "ok"/"error:N" response frees the oldest in-flight line.
/// </summary>
public sealed class GrblStreamer
{
    private readonly ILineTransport _transport;
    private readonly int _bufferSize;
    private readonly object _gate = new();
    private readonly Queue<int> _inFlight = new(); // byte length of each line sent but not yet acknowledged
    private IReadOnlyList<string> _lines = Array.Empty<string>();
    private int _nextToSend;
    private int _bytesInFlight;

    public StreamState State { get; private set; } = StreamState.Idle;
    public int TotalLines => _lines.Count;
    public int Acknowledged { get; private set; }
    public int ErrorCount { get; private set; }

    /// <summary>When true (default) an "error:N" response stops the job — safest for a laser.</summary>
    public bool StopOnError { get; set; } = true;

    public event Action<int, int>? ProgressChanged;            // acknowledged, total
    public event Action<string, string>? ErrorReported;        // offending line, GRBL error text
    public event Action<StreamState>? StateChanged;
    public event Action<GrblStatus>? StatusReceived;
    public event Action<string>? MessageReceived;              // everything else: welcome, [MSG:], ALARM:, $ settings…

    public GrblStreamer(ILineTransport transport, int rxBufferSize = 128)
    {
        _transport = transport;
        _bufferSize = rxBufferSize - 1;
        _transport.LineReceived += OnLine;
    }

    public bool IsBusy => State is StreamState.Running or StreamState.Paused;

    public void Start(IEnumerable<string> gcode)
    {
        lock (_gate)
        {
            if (IsBusy) throw new InvalidOperationException("A job is already running.");
            _lines = gcode.Select(Clean).Where(l => l.Length > 0).ToList();
            _nextToSend = 0;
            _bytesInFlight = 0;
            _inFlight.Clear();
            Acknowledged = 0;
            ErrorCount = 0;
            SetState(StreamState.Running);
            Pump();
        }
        ProgressChanged?.Invoke(Acknowledged, TotalLines);
    }

    /// <summary>Sends a single command outside of a job (e.g. "$H", "$X", jog). Ignored while a job runs.</summary>
    public bool SendCommand(string line)
    {
        lock (_gate)
        {
            if (IsBusy) return false;
            var clean = Clean(line);
            if (clean.Length == 0) return false;
            _inFlight.Enqueue(clean.Length + 1);
            _bytesInFlight += clean.Length + 1;
            _transport.Write(clean + "\n");
            return true;
        }
    }

    /// <summary>Feed hold – motion decelerates and pauses; the laser turns off in laser mode.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (State != StreamState.Running) return;
            _transport.Write("!");
            SetState(StreamState.Paused);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (State != StreamState.Paused) return;
            _transport.Write("~");
            SetState(StreamState.Running);
            Pump();
        }
    }

    /// <summary>Immediate stop: feed hold then soft-reset (Ctrl-X), which clears GRBL's buffers and kills the laser.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _transport.Write("!");
            _transport.Write("\x18");
            _inFlight.Clear();
            _bytesInFlight = 0;
            if (IsBusy) SetState(StreamState.Stopped);
        }
    }

    public void RequestStatus() => _transport.Write("?");

    /// <summary>Jog (GRBL 1.1 $J). Distances in machine mm, relative.</summary>
    public bool Jog(double dx, double dy, double feed) =>
        SendCommand(FormattableString.Invariant($"$J=G91 G21 X{dx:0.###} Y{dy:0.###} F{feed:0}"));

    public void CancelJog() => _transport.Write("\x85");

    private void OnLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0) return;

        if (line.StartsWith('<'))
        {
            if (GrblStatus.TryParse(line, out var st)) StatusReceived?.Invoke(st);
            return;
        }

        bool isOk = line == "ok";
        bool isError = line.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
        if (!isOk && !isError)
        {
            if (line.StartsWith("Grbl ", StringComparison.Ordinal))
            {
                // Controller was reset: anything in flight is gone.
                lock (_gate) { _inFlight.Clear(); _bytesInFlight = 0; }
            }
            if (line.StartsWith("ALARM:", StringComparison.Ordinal))
            {
                lock (_gate) { if (IsBusy) SetState(StreamState.Faulted); }
            }
            MessageReceived?.Invoke(line);
            return;
        }

        bool reportProgress = false;
        string? failedLine = null;
        lock (_gate)
        {
            if (_inFlight.Count > 0) _bytesInFlight -= _inFlight.Dequeue();
            if (IsBusy || State == StreamState.Faulted)
            {
                int ackIndex = Acknowledged;
                Acknowledged++;
                reportProgress = true;
                if (isError)
                {
                    ErrorCount++;
                    failedLine = ackIndex < _lines.Count ? _lines[ackIndex] : "?";
                    if (StopOnError && IsBusy)
                    {
                        _transport.Write("!");
                        _transport.Write("\x18");
                        _inFlight.Clear();
                        _bytesInFlight = 0;
                        SetState(StreamState.Faulted);
                    }
                }
                if (State == StreamState.Running)
                {
                    Pump();
                    if (Acknowledged >= _lines.Count) SetState(StreamState.Completed);
                }
            }
        }
        if (failedLine != null) ErrorReported?.Invoke(failedLine, GrblErrors.Describe(line));
        else if (isError) MessageReceived?.Invoke(GrblErrors.Describe(line));
        if (reportProgress) ProgressChanged?.Invoke(Acknowledged, TotalLines);
    }

    // Caller holds _gate.
    private void Pump()
    {
        if (State != StreamState.Running) return;
        var sb = new StringBuilder();
        while (_nextToSend < _lines.Count)
        {
            int len = _lines[_nextToSend].Length + 1;
            if (_bytesInFlight + len > _bufferSize && _inFlight.Count > 0) break;
            sb.Append(_lines[_nextToSend]).Append('\n');
            _inFlight.Enqueue(len);
            _bytesInFlight += len;
            _nextToSend++;
        }
        if (sb.Length > 0) _transport.Write(sb.ToString());
    }

    private void SetState(StreamState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>Strips comments and whitespace; GRBL rejects lines longer than its line buffer.</summary>
    public static string Clean(string line)
    {
        int semi = line.IndexOf(';');
        if (semi >= 0) line = line[..semi];
        int paren = line.IndexOf('(');
        if (paren >= 0) line = line[..paren];
        return line.Trim();
    }
}
