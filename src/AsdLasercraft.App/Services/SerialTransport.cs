using System.IO.Ports;
using System.Text;
using AsdLasercraft.Core.Grbl;

namespace AsdLasercraft.App.Services;

/// <summary>
/// USB serial link to the GRBL controller. Reading and writing each run on their own thread
/// (see <see cref="QueuedLineTransport"/>); events are raised on the reader thread.
/// </summary>
public sealed class SerialTransport : ILineTransport, IDisposable
{
    private readonly SerialPort _port;
    private QueuedLineTransport? _queue;

    public event Action<string>? LineReceived;

    /// <summary>Raised once if the port fails (cable pulled, driver error, writes stuck).</summary>
    public event Action<string>? Faulted;

    public SerialTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.Latin1,
            NewLine = "\n",
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 100,    // the reader thread wakes up this often to check for shutdown
            WriteTimeout = 5000,  // a write stuck this long means the laser has stopped accepting data
            ReadBufferSize = 64 * 1024,
        };
    }

    public static string[] AvailablePorts() => SerialPort.GetPortNames().OrderBy(p => p).ToArray();

    public bool IsOpen => _port.IsOpen;

    public int QueuedItems => _queue?.QueuedItems ?? 0;

    public void Open()
    {
        _port.Open();
        _queue = new QueuedLineTransport(new PortIo(_port), _port.PortName);
        _queue.LineReceived += l => LineReceived?.Invoke(l);
        _queue.Faulted += m => Faulted?.Invoke(m);
        _queue.Start();
    }

    public void Write(string text) => _queue?.Write(text);

    public void Dispose()
    {
        _queue?.Dispose();
        try { if (_port.IsOpen) _port.Close(); } catch { /* port may already be gone (cable pulled) */ }
        _port.Dispose();
    }

    private sealed class PortIo : IPortIo
    {
        private readonly SerialPort _p;
        public PortIo(SerialPort p) => _p = p;

        public int Read(byte[] buffer, int offset, int count)
        {
            try { return _p.Read(buffer, offset, count); }
            catch (TimeoutException) { return 0; }
        }

        public void Write(byte[] data) => _p.Write(data, 0, data.Length);
    }
}
