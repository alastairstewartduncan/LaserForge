using System.IO.Ports;
using System.Text;
using AsdLasercraft.Core.Grbl;

namespace AsdLasercraft.App.Services;

/// <summary>USB serial link to the GRBL controller. Events are raised on a background thread.</summary>
public sealed class SerialTransport : ILineTransport, IDisposable
{
    private readonly SerialPort _port;
    private readonly StringBuilder _rx = new();
    private readonly object _writeLock = new();

    public event Action<string>? LineReceived;

    public SerialTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            // Latin-1 keeps real-time bytes above 0x7F (e.g. 0x85 jog cancel) intact.
            Encoding = Encoding.Latin1,
            NewLine = "\n",
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 500,
            WriteTimeout = 2000,
        };
        _port.DataReceived += OnData;
    }

    public static string[] AvailablePorts() => SerialPort.GetPortNames().OrderBy(p => p).ToArray();

    public bool IsOpen => _port.IsOpen;

    public void Open() => _port.Open();

    public void Write(string text)
    {
        lock (_writeLock)
        {
            if (_port.IsOpen) _port.Write(text);
        }
    }

    // Windows raises DataReceived on thread-pool threads, so two handlers can run at once. Reading, splitting and
    // dispatching all happen under one lock so chunks are never stitched together out of order (which garbles an
    // "ok" and used to make the streamer wait forever) and lines reach the streamer in the order they arrived.
    private readonly object _readLock = new();

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        lock (_readLock)
        {
            string chunk;
            try
            {
                if (!_port.IsOpen) return;
                chunk = _port.ReadExisting();
            }
            catch (Exception) { return; }

            foreach (char c in chunk)
            {
                if (c == '\n')
                {
                    var line = _rx.ToString().TrimEnd('\r');
                    _rx.Clear();
                    LineReceived?.Invoke(line);
                }
                else _rx.Append(c);
            }
        }
    }

    public void Dispose()
    {
        _port.DataReceived -= OnData;
        try { if (_port.IsOpen) _port.Close(); } catch { /* port may already be gone (cable pulled) */ }
        _port.Dispose();
    }
}
