using System.IO.Ports;
using System.Text;
using LaserForge.Core.Grbl;

namespace LaserForge.App.Services;

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

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        string chunk;
        try { chunk = _port.ReadExisting(); }
        catch (Exception) { return; }

        List<string>? lines = null;
        lock (_rx)
        {
            foreach (char c in chunk)
            {
                if (c == '\n')
                {
                    (lines ??= new()).Add(_rx.ToString().TrimEnd('\r'));
                    _rx.Clear();
                }
                else _rx.Append(c);
            }
        }
        if (lines != null)
            foreach (var l in lines) LineReceived?.Invoke(l);
    }

    public void Dispose()
    {
        _port.DataReceived -= OnData;
        try { if (_port.IsOpen) _port.Close(); } catch { /* port may already be gone (cable pulled) */ }
        _port.Dispose();
    }
}
