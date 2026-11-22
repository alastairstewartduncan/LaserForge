using System.Collections.Concurrent;
using System.Text;

namespace AsdLasercraft.Core.Grbl;

/// <summary>Raw byte access to a serial port. Read returns 0 when nothing arrived within its timeout.</summary>
public interface IPortIo
{
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] data);
}

/// <summary>
/// Line transport with one dedicated reader thread and one dedicated writer thread, the way LaserGRBL does it.
/// <see cref="Write"/> only queues and never blocks, so a slow or stuck write can never hold up the replies
/// coming back (in 0.5.2 both shared locks, and one slow write could freeze the whole job silently).
/// Real-time bytes ('?', '!', '~', Ctrl-X, 0x80+) jump the queue. Any I/O failure raises <see cref="Faulted"/> once.
/// </summary>
public sealed class QueuedLineTransport : ILineTransport, IDisposable
{
    private static readonly Encoding Latin1 = Encoding.Latin1; // keeps real-time bytes above 0x7F intact

    private readonly IPortIo _io;
    private readonly ConcurrentQueue<string> _realtime = new();
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly SemaphoreSlim _pending = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _reader, _writer;
    private int _faulted;

    public event Action<string>? LineReceived;

    /// <summary>Raised once, from a background thread, when reading or writing fails (cable pulled, port gone, write stuck).</summary>
    public event Action<string>? Faulted;

    public QueuedLineTransport(IPortIo io, string name = "serial")
    {
        _io = io;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = name + " reader" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = name + " writer" };
    }

    public void Start()
    {
        _reader.Start();
        _writer.Start();
    }

    /// <summary>Bytes queued but not yet handed to the port (for diagnostics).</summary>
    public int QueuedItems => _realtime.Count + _lines.Count;

    public void Write(string text)
    {
        if (text.Length == 0 || _cts.IsCancellationRequested) return;
        if (text.Length == 1 && IsRealtime(text[0])) _realtime.Enqueue(text);
        else _lines.Enqueue(text);
        _pending.Release();
    }

    public static bool IsRealtime(char c) => c is '?' or '!' or '~' or '\x18' || c >= '\x80';

    private void WriteLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _pending.Wait(ct);
                if (!_realtime.TryDequeue(out var text) && !_lines.TryDequeue(out text)) continue;
                _io.Write(Latin1.GetBytes(text));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("Couldn't send to the laser: " + ex.Message); }
    }

    private void ReadLoop()
    {
        var ct = _cts.Token;
        var buf = new byte[4096];
        var line = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = _io.Read(buf, 0, buf.Length);
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i]; // Latin-1: byte value == char value
                    if (c == '\n')
                    {
                        var s = line.ToString().TrimEnd('\r');
                        line.Clear();
                        LineReceived?.Invoke(s);
                    }
                    else line.Append(c);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Fail("Lost the connection to the laser: " + ex.Message); }
        catch (Exception) { /* closing */ }
    }

    private void Fail(string message)
    {
        if (_cts.IsCancellationRequested || Interlocked.Exchange(ref _faulted, 1) != 0) return;
        Faulted?.Invoke(message);
    }

    /// <summary>Stops both threads. Call before closing the port; the reader exits at its next read timeout.</summary>
    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _writer.Join(500);
        _reader.Join(500);
    }
}
