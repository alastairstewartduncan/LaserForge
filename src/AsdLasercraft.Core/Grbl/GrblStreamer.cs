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
    private long _lastActivityMs;   // last time a line was sent or acknowledged
    private int _idleReports;       // consecutive "Idle" status reports while lines are in flight
    private long _lastRxMs;         // last time anything at all (ok, status report, message) came back
    private int _pollsUnanswered;   // status polls sent since anything came back

    /// <summary>
    /// If GRBL reports Idle for this long while lines are still counted as in flight, their "ok"s were lost
    /// (garbled on the serial link) and the stream is resynchronised instead of waiting forever.
    /// </summary>
    public int StallRecoveryMs { get; set; } = 1500;

    /// <summary>
    /// If nothing at all (not even a reply to the 200 ms status poll) comes back for this long during a job, the USB
    /// link has gone (cable, interference, controller browned out) and the job is stopped with an explanation.
    /// </summary>
    public int LinkTimeoutMs { get; set; } = 4000;

    /// <summary>Millisecond clock; replaceable in tests.</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>How many lost acknowledgements were recovered during the current job.</summary>
    public int RecoveredAcks { get; private set; }

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

    /// <summary>
    /// Raised (outside any lock) when the streamer had to recover or stop a job by itself. The argument says why;
    /// save <see cref="Diagnostics"/> at that point so a stall can be investigated afterwards.
    /// </summary>
    public event Action<string>? Incident;

    // ------------------------------------------------------------------ traffic log for diagnostics
    private const int TrafficCapacity = 600;
    private readonly Queue<string> _traffic = new();
    private string? _lastStatusLogged;

    // Caller holds _gate.
    private void Trace(char dir, string text)
    {
        if (_traffic.Count >= TrafficCapacity) _traffic.Dequeue();
        _traffic.Enqueue($"{Clock(),12} {dir} {text}");
    }

    /// <summary>The streamer's counters plus the last few hundred lines sent ('>'), received ('<') and noted ('#').</summary>
    public string Diagnostics(string? reason = null)
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Reason: {reason ?? "(none)"}");
            sb.AppendLine($"State {State}, acknowledged {Acknowledged:N0}, sent {_nextToSend:N0} of {TotalLines:N0}, " +
                          $"in flight {_inFlight.Count} lines / {_bytesInFlight} bytes (buffer {_bufferSize}), " +
                          $"errors {ErrorCount}, recovered acks {RecoveredAcks}");
            sb.AppendLine($"Clock {Clock()} ms, last ack/send {_lastActivityMs}, last reply {_lastRxMs}, unanswered polls {_pollsUnanswered}");
            sb.AppendLine("Columns: clock ms, direction (> sent, < received, # note), text. Repeated status reports are skipped.");
            foreach (var t in _traffic) sb.AppendLine(t);
            return sb.ToString();
        }
    }

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
            RecoveredAcks = 0;
            _idleReports = 0;
            _lastActivityMs = Clock();
            _lastRxMs = _lastActivityMs;
            _pollsUnanswered = 0;
            Trace('#', $"job start: {_lines.Count:N0} lines");
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
            Trace('>', clean);
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

    /// <summary>Sends a status poll ('?'). Also the link watchdog: a job that hears nothing back is stopped, not left hanging.</summary>
    /// <summary>Stops a running job because the link failed (raised by the transport). No-op when nothing runs.</summary>
    public void Abort(string reason)
    {
        string? msg = null;
        lock (_gate)
        {
            Trace('#', "abort: " + reason);
            if (IsBusy)
            {
                msg = $"{reason} The job was stopped at {Where()}.";
                _inFlight.Clear(); _bytesInFlight = 0;
                SetState(StreamState.Faulted);
            }
        }
        if (msg != null) { MessageReceived?.Invoke(msg); Incident?.Invoke(msg); }
    }

    public void RequestStatus()
    {
        string? lost = null;
        lock (_gate)
        {
            // Needs both time and unanswered polls, so a UI hiccup that delayed the polls can't trip it.
            if (IsBusy && ++_pollsUnanswered >= 10 && Clock() - _lastRxMs > LinkTimeoutMs)
            {
                lost = $"No reply from the laser for {LinkTimeoutMs / 1000} s at {Where()}, so the job was stopped. " +
                       "The USB link dropped or the controller lost power. This usually happens when the laser fires hard: " +
                       "check the USB cable (short, with a ferrite bead), use a different USB port, and check the power supply.";
                Trace('#', "link watchdog: no reply");
                SetState(StreamState.Faulted);
            }
        }
        if (lost != null) { MessageReceived?.Invoke(lost); Incident?.Invoke(lost); return; }
        _transport.Write("?");
    }

    // Caller holds _gate. Where the job had got to, for messages.
    private string Where()
    {
        int i = Math.Min(Acknowledged, Math.Max(0, _lines.Count - 1));
        string near = _lines.Count > 0 ? $" (\"{_lines[i]}\")" : "";
        return $"line {Acknowledged + 1:N0} of {_lines.Count:N0}{near}";
    }

    /// <summary>Jog (GRBL 1.1 $J). Distances in machine mm, relative.</summary>
    public bool Jog(double dx, double dy, double feed) =>
        SendCommand(FormattableString.Invariant($"$J=G91 G21 X{dx:0.###} Y{dy:0.###} F{feed:0}"));

    public void CancelJog() => _transport.Write("\x85");

    private void OnLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0) return;
        lock (_gate)
        {
            _lastRxMs = Clock();
            _pollsUnanswered = 0;
            if (!line.StartsWith('<')) Trace('<', line);
            else
            {
                // Status reports arrive 5 times a second; log only when the state word changes.
                int bar = line.IndexOf('|');
                string word = bar > 0 ? line[..bar] : line;
                if (word != _lastStatusLogged) { Trace('<', line); _lastStatusLogged = word; }
            }
        }

        if (line.StartsWith('<'))
        {
            if (GrblStatus.TryParse(line, out var st))
            {
                string? alarm = null;
                lock (_gate)
                {
                    if (IsBusy && st.State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase))
                    {
                        alarm = $"The laser went into Alarm at {Where()}, so the job was stopped. Check the console for the alarm number.";
                        Trace('#', "alarm status during job");
                        _inFlight.Clear(); _bytesInFlight = 0;
                        SetState(StreamState.Faulted);
                    }
                }
                if (alarm != null) { StatusReceived?.Invoke(st); MessageReceived?.Invoke(alarm); Incident?.Invoke(alarm); return; }
                string? recovered = CheckStall(st);
                StatusReceived?.Invoke(st);
                if (recovered != null)
                {
                    MessageReceived?.Invoke(recovered);
                    Incident?.Invoke(recovered);
                    ProgressChanged?.Invoke(Acknowledged, TotalLines);
                }
            }
            return;
        }

        bool isOk = line == "ok";
        bool isError = line.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
        if (!isOk && !isError)
        {
            if (line.StartsWith("Grbl", StringComparison.Ordinal))
            {
                // Controller was reset: anything in flight is gone. If a job was running, it can't carry on: the
                // controller has forgotten its position and modes. Before, the app stayed "Running" here forever.
                string? reset = null;
                lock (_gate)
                {
                    _inFlight.Clear(); _bytesInFlight = 0;
                    if (IsBusy)
                    {
                        reset = $"The laser controller restarted by itself at {Where()}, so the job was stopped. " +
                                "That's electrical, not the design: usually the USB link or the controller's power dropping " +
                                "when the laser fires hard. Try a short USB cable with a ferrite bead, a different USB port, " +
                                "and check the power supply and its connector.";
                        Trace('#', "controller reset during job");
                        SetState(StreamState.Faulted);
                    }
                }
                MessageReceived?.Invoke(line);
                if (reset != null) { MessageReceived?.Invoke(reset); Incident?.Invoke(reset); }
                return;
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
            _lastActivityMs = Clock();
            _idleReports = 0;
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

    /// <summary>
    /// Lost-acknowledgement recovery. Character counting stalls for good if any "ok" never arrives: the app keeps
    /// thinking GRBL's buffer is full while GRBL has finished everything and sits Idle. GRBL only reports Idle when
    /// its planner is empty and it has nothing left to parse, so after a quiet spell of Idle reports every line
    /// still counted as in flight has in fact been executed; count them as done and carry on streaming.
    /// </summary>
    private string? CheckStall(GrblStatus st)
    {
        lock (_gate)
        {
            if (State != StreamState.Running) { _idleReports = 0; return null; }
            if (_inFlight.Count == 0 && _nextToSend >= _lines.Count) { _idleReports = 0; return null; }
            if (!string.Equals(st.State, "Idle", StringComparison.OrdinalIgnoreCase)) { _idleReports = 0; return null; }
            _idleReports++;
            if (_idleReports < 2 || Clock() - _lastActivityMs < StallRecoveryMs) return null;

            if (_inFlight.Count == 0)
            {
                // Nothing awaiting a reply, lines left to send, and yet nothing went out: never expected, but if it
                // happens the job must not sit there silently. Start sending again.
                Trace('#', "idle with nothing in flight: resuming sending");
                _idleReports = 0;
                _lastActivityMs = Clock();
                Pump();
                return $"The job had stopped sending at {Where()} with nothing outstanding; sending resumed.";
            }

            int lost = _inFlight.Count;
            Trace('#', $"idle with {lost} line(s) unacknowledged: counting them as done");
            _inFlight.Clear();
            _bytesInFlight = 0;
            Acknowledged = Math.Min(_nextToSend, Acknowledged + lost);
            RecoveredAcks += lost;
            _idleReports = 0;
            _lastActivityMs = Clock();
            Pump();
            if (Acknowledged >= _lines.Count) SetState(StreamState.Completed);
            return $"Recovered {lost} lost acknowledgement(s) from the controller; job continued.";
        }
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
            Trace('>', $"[{_nextToSend + 1}] {_lines[_nextToSend]}");
            _inFlight.Enqueue(len);
            _bytesInFlight += len;
            _nextToSend++;
        }
        if (sb.Length > 0)
        {
            _lastActivityMs = Clock();
            _transport.Write(sb.ToString());
        }
    }

    private void SetState(StreamState s)
    {
        if (State == s) return;
        Trace('#', $"state {State} -> {s}");
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
