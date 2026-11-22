using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Generators;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Grbl;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Import;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Serialization;

namespace AsdLasercraft.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute { }

internal static class Program
{
    private static int Main()
    {
        int passed = 0, failed = 0;
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name);
        foreach (var t in tests)
        {
            try
            {
                t.Invoke(null, null);
                passed++;
                Console.WriteLine($"  PASS  {t.DeclaringType!.Name}.{t.Name}");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {t.DeclaringType!.Name}.{t.Name}: {ex.InnerException?.Message}");
                // On GitHub Actions, also raise an annotation so failures show on the run summary.
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                    Console.WriteLine($"::error title=Test failed: {t.DeclaringType!.Name}.{t.Name}::{Escape(ex.InnerException?.Message ?? "")}");
            }
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Console.WriteLine($"::notice title=Core tests::{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static string Escape(string s) => s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
}

internal static class Assert
{
    public static void True(bool cond, string msg = "expected true") { if (!cond) throw new Exception(msg); }
    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but was <{actual}>");
    }
    public static void Near(double expected, double actual, double tol, string what = "")
    {
        if (Math.Abs(expected - actual) > tol) throw new Exception($"{what} expected {expected} ± {tol} but was {actual}");
    }
}

/// <summary>Helpers to read G-code back for assertions.</summary>
internal static class G
{
    public static IEnumerable<(string Line, double? X, double? Y, double? S)> Moves(IEnumerable<string> lines)
    {
        double? s = null;
        foreach (var l in lines)
        {
            if (l.StartsWith(";")) continue;
            double? Get(char c)
            {
                var m = Regex.Match(l, c + @"(-?[\d.]+)");
                return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            }
            var ns = Get('S');
            if (ns != null) s = ns;
            yield return (l, Get('X'), Get('Y'), s);
        }
    }
}

internal static class GeometryTests
{
    [Test] static void TransformComposeOrder()
    {
        var t = Transform2D.Translate(10, 0).Then(Transform2D.Scale(2, 2));
        var p = t.Apply(new Vec2(1, 1));
        Assert.Equal(new Vec2(22, 2), p, "translate-then-scale");
    }

    [Test] static void RotateAboutCentre()
    {
        var p = Transform2D.Rotate(90, new Vec2(5, 5)).Apply(new Vec2(10, 5));
        Assert.Near(5, p.X, 1e-9); Assert.Near(10, p.Y, 1e-9);
    }

    [Test] static void PointInPolygonEvenOdd()
    {
        var sq = ShapeFactory.Rectangle(0, 0, 10, 10).Paths[0];
        Assert.True(sq.ContainsPoint(new Vec2(5, 5)));
        Assert.True(!sq.ContainsPoint(new Vec2(15, 5)));
    }

    [Test] static void ResizeKeepsTopLeft()
    {
        var r = ShapeFactory.Rectangle(20, 30, 10, 5);
        r.Resize(40, 10);
        var b = r.GetBounds();
        Assert.Near(20, b.MinX, 1e-9); Assert.Near(30, b.MinY, 1e-9);
        Assert.Near(40, b.Width, 1e-9); Assert.Near(10, b.Height, 1e-9);
    }

    [Test] static void ArcQuarterCircleEndsExactly()
    {
        var pts = Curves.SvgArc(new Vec2(10, 0), 10, 10, 0, false, true, new Vec2(0, 10)).ToList();
        Assert.Equal(new Vec2(0, 10), pts[^1]);
        // Every point should lie on the circle centred at origin.
        foreach (var p in pts) Assert.Near(10, p.Length, 1e-6, "radius");
    }
}

internal static class GcodeTests
{
    private static LaserProject OneShape(Shape s, LayerMode mode, Action<Layer>? configure = null)
    {
        var p = new LaserProject();
        p.Machine.FlipY = false;
        p.Machine.ReturnToOrigin = false;
        var layer = LaserProject.DefaultLayer(mode);
        layer.Passes = 1;
        layer.OverscanMm = 0;
        configure?.Invoke(layer);
        p.Layers.Add(layer);
        s.LayerId = layer.Id;
        p.Shapes.Add(s);
        return p;
    }

    [Test] static void LineModeTracesClosedRectangle()
    {
        var p = OneShape(ShapeFactory.Rectangle(10, 10, 20, 10), LayerMode.Line, l => { l.PowerPercent = 80; l.SpeedMmMin = 500; });
        var job = GcodeGenerator.Generate(p);
        Assert.True(job.Lines.Contains("G21") && job.Lines.Contains("G90"), "units/absolute header");
        Assert.True(job.Lines.Any(l => l.Contains("S800")), "80% of S1000");
        Assert.True(job.Lines.Any(l => l.Contains("F500")), "feed");
        Assert.Near(60, job.CutDistanceMm, 1e-6, "perimeter");
    }

    [Test] static void FlipYMapsToMachine()
    {
        var p = OneShape(ShapeFactory.Line(new Vec2(0, 0), new Vec2(0, 100)), LayerMode.Line);
        p.Machine.FlipY = true; p.Machine.BedHeightMm = 400;
        var job = GcodeGenerator.Generate(p);
        Assert.True(job.Lines.Any(l => l.Contains("Y400")), "project y=0 → machine y=400");
        Assert.True(job.Lines.Any(l => l.Contains("Y300")), "project y=100 → machine y=300");
    }

    [Test] static void InnerPathsCutBeforeOuter()
    {
        var outer = ShapeFactory.Rectangle(0, 0, 100, 100).Paths[0];
        var inner = ShapeFactory.Rectangle(40, 40, 10, 10).Paths[0];
        var ordered = PathOrdering.Order(new[] { outer, inner }, Vec2.Zero);
        Assert.True(ordered[0].Bounds.Width < 20, "hole first");
    }

    [Test] static void FillHatchesWithHole()
    {
        // A 10x10 square with a 4x4 hole: the centre scanline should give two segments.
        var shape = new VectorShape(new[]
        {
            ShapeFactory.Rectangle(0, 0, 10, 10).Paths[0],
            ShapeFactory.Rectangle(3, 3, 4, 4).Paths[0],
        });
        var rows = ScanlineFill.Hatch(shape.Paths, 1.0);
        Assert.Equal(10, rows.Count, "row count");
        var mid = rows.First(r => Math.Abs(r.Y - 5.5) < 1e-9);
        Assert.Equal(2, mid.Segments.Count, "segments around hole");
        Assert.Near(3, mid.Segments[0].X1, 1e-9); Assert.Near(7, mid.Segments[1].X0, 1e-9);
    }

    [Test] static void FillBurnDistanceMatchesArea()
    {
        var p = OneShape(ShapeFactory.Rectangle(0, 0, 20, 10), LayerMode.Fill, l => l.LineIntervalMm = 0.5);
        var job = GcodeGenerator.Generate(p);
        // Area / interval = burned length.
        Assert.Near(20 * 10 / 0.5, job.CutDistanceMm, 1e-6, "fill length");
    }

    [Test] static void RasterRunLengthEncodes()
    {
        // 4x1 image: black, black, white, black at 1 mm/pixel.
        var img = new ImageShape { X = 0, Y = 0, WidthMm = 4, HeightMm = 1, PixelWidth = 4, PixelHeight = 1, Gray = new byte[] { 0, 0, 255, 0 }, Dither = DitherMode.Threshold };
        var p = OneShape(img, LayerMode.Image, l => { l.LineIntervalMm = 1; l.PowerPercent = 50; });
        var job = GcodeGenerator.Generate(p);
        Assert.True(job.Lines.Any(l => l == "X2 S500" || l.StartsWith("G1 X2 S500")), "first run 0→2 at S500: " + string.Join(" | ", job.Lines));
        Assert.True(job.Lines.Any(l => l.StartsWith("X3 S0")), "white gap");
        Assert.True(job.Lines.Any(l => l.StartsWith("X4 S500")), "last pixel");
        Assert.Near(3, job.CutDistanceMm, 1e-9, "burned length");
    }

    [Test] static void GrayscaleMapsPowerRange()
    {
        var img = new ImageShape { X = 0, Y = 0, WidthMm = 2, HeightMm = 1, PixelWidth = 2, PixelHeight = 1, Gray = new byte[] { 0, 128 }, Dither = DitherMode.Grayscale };
        var p = OneShape(img, LayerMode.Image, l => { l.LineIntervalMm = 1; l.PowerPercent = 100; l.MinPowerPercent = 10; });
        var job = GcodeGenerator.Generate(p);
        Assert.True(job.Lines.Any(l => l.Contains("S1000")), "black = max");
        Assert.True(job.Lines.Any(l => l.Contains("S548")), "mid grey 128 → 100 + 900 * (1 - 128/255) ≈ 548");
    }

    [Test] static void FrameTracesBoundingBox()
    {
        var p = OneShape(ShapeFactory.Rectangle(10, 20, 30, 40), LayerMode.Line);
        var job = GcodeGenerator.GenerateFrame(p, 0, 3000);
        Assert.Near(2 * (30 + 40), job.CutDistanceMm + job.TravelDistanceMm, 1e-6, "perimeter traced");
        Assert.True(job.Lines.Count(l => l.StartsWith("G1") || l.StartsWith("X")) >= 1, "trace moves present");
        Assert.True(!job.Lines.Any(l => Regex.IsMatch(l, @"S[1-9]")), "laser off when power 0");
        Assert.Near(30, job.Bounds.Width, 1e-6); Assert.Near(40, job.Bounds.Height, 1e-6);
    }

    [Test] static void PassesRepeat()
    {
        var p = OneShape(ShapeFactory.Rectangle(0, 0, 10, 10), LayerMode.Line, l => l.Passes = 3);
        var job = GcodeGenerator.Generate(p);
        Assert.Near(120, job.CutDistanceMm, 1e-6, "3 x perimeter");
    }
}

internal static class ImagingTests
{
    [Test] static void DitherPreservesAverageTone()
    {
        var gray = Enumerable.Repeat((byte)128, 64 * 64).ToArray();
        foreach (var mode in new[] { DitherMode.FloydSteinberg, DitherMode.Jarvis, DitherMode.Stucki })
        {
            var d = ImageProcessor.Dither(gray, 64, 64, mode);
            double black = d.Count(v => v == 0) / (double)d.Length;
            Assert.Near(0.5, black, 0.05, mode.ToString());
            Assert.True(d.All(v => v is 0 or 255), "binary output");
        }
    }

    [Test] static void ResampleDownAverages()
    {
        var src = new byte[] { 0, 255, 0, 255 };
        var dst = ImageProcessor.Resample(src, 2, 2, 1, 1);
        Assert.Near(128, dst[0], 1);
    }

    [Test] static void InvertFlips()
    {
        var d = ImageProcessor.Adjust(new byte[] { 0, 255 }, 0, 0, 1, true);
        Assert.Equal((byte)255, d[0]); Assert.Equal((byte)0, d[1]);
    }

    [Test] static void Rotate90Clockwise()
    {
        var img = new ImageShape { X = 0, Y = 0, WidthMm = 20, HeightMm = 10, PixelWidth = 2, PixelHeight = 1, Gray = new byte[] { 1, 2 } };
        img.Rotate90(true);
        Assert.Equal(1, img.PixelWidth); Assert.Equal(2, img.PixelHeight);
        Assert.Equal((byte)1, img.Gray[0]); Assert.Equal((byte)2, img.Gray[1]);
        Assert.Near(10, img.WidthMm, 1e-9); Assert.Near(5, img.X, 1e-9);
    }
}

internal static class SvgTests
{
    [Test] static void ImportsMmViewBox()
    {
        var svg = """<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="50mm" viewBox="0 0 200 100"><rect x="20" y="10" width="40" height="20"/></svg>""";
        var paths = SvgImporter.Import(svg);
        Assert.Equal(1, paths.Count);
        var b = paths[0].Bounds;
        Assert.Near(10, b.MinX, 1e-9); Assert.Near(20, b.Width, 1e-9); Assert.Near(10, b.Height, 1e-9);
    }

    [Test] static void PathCommandsAndGroupTransform()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 100 100">
              <g transform="translate(10,10)">
                <path d="M0 0 h10 v10 h-10 z M20 0 C20 10 30 10 30 0"/>
                <circle cx="50" cy="50" r="5"/>
              </g>
            </svg>
            """;
        var paths = SvgImporter.Import(svg);
        Assert.Equal(3, paths.Count);
        Assert.True(paths[0].Closed, "z closes");
        Assert.Near(10, paths[0].Bounds.MinX, 1e-9);
        Assert.Near(60, paths[2].Bounds.Center.X, 1e-6);
    }

    [Test] static void ImplicitLinetoAfterMove()
    {
        var p = SvgImporter.ParsePath("M 0 0 10 0 10 10 Z");
        Assert.Equal(3, p[0].Points.Count);
        Assert.True(p[0].Closed);
    }
}

internal static class GrblTests
{
    private sealed class FakePort : ILineTransport
    {
        public readonly List<string> Writes = new();
        public event Action<string>? LineReceived;
        public void Write(string text) => Writes.Add(text);
        public void Reply(string line) => LineReceived?.Invoke(line);
        public int LinesSent => Writes.Sum(w => w.Count(c => c == '\n'));
    }

    [Test] static void CharacterCountingNeverOverflowsBuffer()
    {
        var port = new FakePort();
        var s = new GrblStreamer(port, 128);
        var lines = Enumerable.Range(0, 100).Select(i => $"G1 X{i}.123 Y{i}.456 S500 F3000").ToList(); // ~31 bytes each
        s.Start(lines);
        int firstBurst = port.LinesSent;
        Assert.True(firstBurst is >= 3 and <= 4, $"first burst fits in 127 bytes, sent {firstBurst}");
        while (s.State == StreamState.Running) port.Reply("ok");
        Assert.Equal(StreamState.Completed, s.State);
        Assert.Equal(100, s.Acknowledged);
        Assert.Equal(100, port.LinesSent);
    }

    [Test] static void ErrorStopsJobAndSoftResets()
    {
        var port = new FakePort();
        var s = new GrblStreamer(port);
        string? reported = null;
        s.ErrorReported += (line, err) => reported = line + " → " + err;
        s.Start(new[] { "G1 X1 F100", "G1 X2", "G1 X3" });
        port.Reply("error:22");
        Assert.Equal(StreamState.Faulted, s.State);
        Assert.True(port.Writes.Contains("\x18"), "soft reset sent");
        Assert.True(reported!.Contains("Feed rate"), reported);
    }

    [Test] static void CommentsStripped()
    {
        Assert.Equal("G0 X1", GrblStreamer.Clean("G0 X1 ; move"));
        Assert.Equal("", GrblStreamer.Clean("; header"));
    }

    [Test] static void ParsesStatusReport()
    {
        Assert.True(GrblStatus.TryParse("<Run|MPos:12.500,3.000,0.000|FS:3000,500|WCO:2.500,0.000,0.000>", out var st));
        Assert.Equal("Run", st.State);
        Assert.Equal(new Vec2(12.5, 3), st.MachinePos);
        Assert.Equal(new Vec2(10, 3), st.WorkPos);
        Assert.Near(500, st.Spindle, 1e-9);
    }

    [Test] static void LostAcksRecoverWhenControllerGoesIdle()
    {
        // A garbled "ok" used to leave its bytes counted as in flight forever: after ~10 of them the
        // streamer stopped sending while GRBL sat Idle, and the job hung half way (seen on a dithered photo).
        long now = 0;
        var port = new FakePort();
        var s = new GrblStreamer(port, 128) { Clock = () => now };
        string? log = null;
        s.MessageReceived += m => log = m;
        s.Start(Enumerable.Range(0, 400).Select(i => $"X{i}.127 S{(i % 2) * 400}"));

        // Fake GRBL: answers each line it has received exactly once; every 7th answer arrives garbled.
        // When it has nothing left to answer it is Idle and only answers the 200 ms status poll.
        int answered = 0;
        for (int guard = 0; s.State == StreamState.Running && guard < 100_000; guard++)
        {
            if (answered < port.LinesSent)
            {
                answered++;
                port.Reply(answered % 7 == 0 ? "ko" : "ok");
                now += 2;
            }
            else
            {
                now += 200;
                port.Reply("<Idle|MPos:0,0,0|FS:0,0>");
            }
        }
        Assert.Equal(StreamState.Completed, s.State);
        Assert.Equal(400, port.LinesSent);
        Assert.Equal(400, s.Acknowledged);
        Assert.True(s.RecoveredAcks > 0, "recovered lost acks");
        Assert.True(log != null && log.Contains("Recovered"), log ?? "no log");
    }

    [Test] static void OldBehaviourWouldStallWithoutStatusPolls()
    {
        // Same garbling, but no status reports: the streamer must not guess, it just waits (documents the trigger).
        var port = new FakePort();
        var s = new GrblStreamer(port, 128);
        s.Start(Enumerable.Range(0, 400).Select(i => $"X{i}.127 S{(i % 2) * 400}"));
        int answered = 0;
        while (answered < port.LinesSent) { answered++; port.Reply(answered % 7 == 0 ? "ko" : "ok"); }
        Assert.Equal(StreamState.Running, s.State);
        Assert.True(port.LinesSent < 400, $"stalled at {port.LinesSent}");
    }

    [Test] static void NoRecoveryWhileMachineIsBusy()
    {
        long now = 0;
        var port = new FakePort();
        var s = new GrblStreamer(port, 128) { Clock = () => now };
        s.Start(Enumerable.Range(0, 50).Select(i => $"G1 X{i} F100"));
        int sent = port.LinesSent;
        for (int i = 0; i < 20; i++) { now += 1000; port.Reply("<Run|MPos:0,0,0|FS:100,0>"); }
        Assert.Equal(sent, port.LinesSent);
        Assert.Equal(0, s.Acknowledged);
        Assert.Equal(0, s.RecoveredAcks);
    }

    [Test] static void NoRecoveryWhilePaused()
    {
        long now = 0;
        var port = new FakePort();
        var s = new GrblStreamer(port, 128) { Clock = () => now };
        s.Start(Enumerable.Range(0, 50).Select(i => $"G1 X{i} F100"));
        s.Pause();
        for (int i = 0; i < 20; i++) { now += 1000; port.Reply("<Idle|MPos:0,0,0|FS:0,0>"); }
        Assert.Equal(StreamState.Paused, s.State);
        Assert.Equal(0, s.RecoveredAcks);
    }

    [Test] static void ControllerResetMidJobStopsTheJob()
    {
        // A reset (USB/power glitch) used to clear the in-flight count but leave the job "Running" forever.
        var port = new FakePort();
        var s = new GrblStreamer(port, 128);
        var log = new List<string>();
        s.MessageReceived += log.Add;
        s.Start(Enumerable.Range(0, 200).Select(i => $"G1 X{i} S400 F3000"));
        for (int i = 0; i < 30; i++) port.Reply("ok");
        port.Reply("Grbl 1.1h ['$' for help]");
        Assert.Equal(StreamState.Faulted, s.State);
        Assert.True(log.Any(m => m.Contains("restarted") && m.Contains("line 31")), string.Join(" | ", log));
    }

    [Test] static void SilentLinkStopsTheJob()
    {
        long now = 0;
        var port = new FakePort();
        var s = new GrblStreamer(port, 128) { Clock = () => now };
        var log = new List<string>();
        s.MessageReceived += log.Add;
        s.Start(Enumerable.Range(0, 200).Select(i => $"G1 X{i} S400 F3000"));
        for (int i = 0; i < 10; i++) { now += 200; port.Reply("ok"); s.RequestStatus(); }
        Assert.Equal(StreamState.Running, s.State);
        for (int i = 0; i < 25; i++) { now += 200; s.RequestStatus(); } // USB gone: nothing comes back
        Assert.Equal(StreamState.Faulted, s.State);
        Assert.True(log.Any(m => m.Contains("No reply")), string.Join(" | ", log));
    }

    [Test] static void AlarmStatusMidJobStopsTheJob()
    {
        var port = new FakePort();
        var s = new GrblStreamer(port, 128);
        s.Start(Enumerable.Range(0, 200).Select(i => $"G1 X{i} S400 F3000"));
        port.Reply("<Alarm|MPos:0,0,0|FS:0,0>");
        Assert.Equal(StreamState.Faulted, s.State);
    }

    private sealed class FakeIo : IPortIo
    {
        public readonly System.Collections.Concurrent.BlockingCollection<byte[]> Incoming = new();
        public readonly List<string> Written = new();
        public readonly ManualResetEventSlim WriteGate = new(true);   // reset it to make writes hang
        public Exception? WriteError;
        public int Read(byte[] buffer, int offset, int count)
        {
            if (!Incoming.TryTake(out var chunk, 20)) return 0;
            Array.Copy(chunk, 0, buffer, offset, chunk.Length);
            return chunk.Length;
        }
        public void Write(byte[] data)
        {
            if (WriteError != null) throw WriteError;
            WriteGate.Wait();
            lock (Written) Written.Add(System.Text.Encoding.Latin1.GetString(data));
        }
        public void Send(string s) => Incoming.Add(System.Text.Encoding.Latin1.GetBytes(s));
    }

    private static bool WaitFor(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(5); }
        return true;
    }

    [Test] static void StuckWriteDoesNotBlockReplies()
    {
        // 0.5.2 read and wrote under shared locks, so one slow write froze the replies too and the job hung silently.
        var io = new FakeIo();
        using var t = new QueuedLineTransport(io);
        var got = new List<string>();
        t.LineReceived += l => { lock (got) got.Add(l); };
        t.Start();
        io.WriteGate.Reset();                  // the port stops accepting data
        t.Write("G1 X1\n");                    // must return at once, not block the caller
        t.Write("G1 X2\n");
        io.Send("<Idle|MPos:0,0,0>\r\nok\n");
        Assert.True(WaitFor(() => { lock (got) return got.Count == 2; }), "replies still arrive while a write is stuck");
        Assert.Equal("ok", got[1]);
        io.WriteGate.Set();
        Assert.True(WaitFor(() => { lock (io.Written) return io.Written.Count == 2; }), "queued lines go out once the port accepts data");
        Assert.Equal("G1 X1\n", io.Written[0]);
    }

    [Test] static void SplitChunksAreJoinedInOrder()
    {
        var io = new FakeIo();
        using var t = new QueuedLineTransport(io);
        var got = new List<string>();
        t.LineReceived += l => { lock (got) got.Add(l); };
        t.Start();
        io.Send("o"); io.Send("k\r\nerr"); io.Send("or:2\n<Run|M"); io.Send("Pos:1,2,3>\n");
        Assert.True(WaitFor(() => { lock (got) return got.Count == 3; }));
        Assert.Equal("ok", got[0]);
        Assert.Equal("error:2", got[1]);
        Assert.Equal("<Run|MPos:1,2,3>", got[2]);
    }

    [Test] static void RealtimeBytesJumpTheQueue()
    {
        var io = new FakeIo();
        using var t = new QueuedLineTransport(io);
        io.WriteGate.Reset();
        t.Start();
        t.Write("G1 X1\n");
        Assert.True(WaitFor(() => t.QueuedItems == 0));   // writer has taken the first item and is stuck on it
        t.Write("G1 X2\n");
        t.Write("!");
        t.Write("\x18");
        io.WriteGate.Set();
        Assert.True(WaitFor(() => { lock (io.Written) return io.Written.Count == 4; }));
        Assert.Equal("!", io.Written[1]);
        Assert.Equal("\x18", io.Written[2]);
        Assert.Equal("G1 X2\n", io.Written[3]);
    }

    [Test] static void WriteFailureFaultsTheJob()
    {
        var io = new FakeIo { WriteError = new IOException("The device is not connected.") };
        using var t = new QueuedLineTransport(io);
        var s = new GrblStreamer(t, 128);
        string? incident = null;
        s.Incident += r => incident = r;
        t.Faulted += m => s.Abort(m);
        t.Start();
        s.Start(Enumerable.Range(0, 50).Select(i => $"G1 X{i} S400 F3000"));
        Assert.True(WaitFor(() => s.State == StreamState.Faulted), "job stops when the port fails");
        Assert.True(incident != null && incident.Contains("not connected"), incident ?? "no incident");
    }

    [Test] static void DiagnosticsRecordTraffic()
    {
        long now = 0;
        var port = new FakePort();
        var s = new GrblStreamer(port, 128) { Clock = () => now };
        string? incident = null;
        s.Incident += r => incident = r;
        s.Start(Enumerable.Range(0, 200).Select(i => $"G1 X{i} S400 F3000"));
        port.Reply("ok");
        port.Reply("Grbl 1.1h ['$' for help]");
        Assert.True(incident != null && incident.Contains("restarted"));
        var d = s.Diagnostics(incident);
        Assert.True(d.Contains("> [1] G1 X0 S400 F3000"), d);
        Assert.True(d.Contains("< ok"), d);
        Assert.True(d.Contains("< Grbl 1.1h"), d);
        Assert.True(d.Contains("state Running -> Faulted"), d);
    }

    [Test] static void PauseResumeSendRealtime()
    {
        var port = new FakePort();
        var s = new GrblStreamer(port);
        s.Start(new[] { "G1 X1 F100" });
        s.Pause();
        Assert.Equal(StreamState.Paused, s.State);
        s.Resume();
        Assert.True(port.Writes.Contains("!") && port.Writes.Contains("~"));
    }
}

internal static class ProjectTests
{
    [Test] static void VersionAndAuthorStamped()
    {
        Assert.True(Regex.IsMatch(AppInfo.Version, @"^\d+\.\d+\.\d+$"), "semantic version: " + AppInfo.Version);
        Assert.Equal("Alastair Stewart Duncan", AppInfo.Author);
        Assert.Equal("alastair@aduncan.co.uk", AppInfo.AuthorEmail);
        var job = GcodeGenerator.Generate(LaserProject.CreateDefault());
        Assert.True(job.Lines[0].Contains(AppInfo.ShortName + " " + AppInfo.Version), job.Lines[0]);
    }

    [Test] static void RoundTripsJson()
    {
        var p = LaserProject.CreateDefault();
        var r = ShapeFactory.Rectangle(1, 2, 3, 4); r.LayerId = p.Layers[0].Id; p.Shapes.Add(r);
        var img = new ImageShape { WidthMm = 5, HeightMm = 5, PixelWidth = 1, PixelHeight = 1, Gray = new byte[] { 42 }, LayerId = p.Layers[1].Id };
        p.Shapes.Add(img);
        var back = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p));
        Assert.Equal(2, back.Shapes.Count);
        Assert.True(back.Shapes[0] is VectorShape v && v.Paths[0].Points.Count == 4, "vector");
        Assert.True(back.Shapes[1] is ImageShape i && i.Gray[0] == 42, "image bytes");
        Assert.Equal(LayerMode.Fill, back.Layers[0].Mode);
    }

    [Test] static void UndoRedo()
    {
        var undo = new UndoStack();
        var p = LaserProject.CreateDefault();
        undo.Push(p);
        var r = ShapeFactory.Rectangle(0, 0, 1, 1); r.LayerId = p.Layers[0].Id; p.Shapes.Add(r);
        var before = undo.Undo(p)!;
        Assert.Equal(0, before.Shapes.Count);
        var again = undo.Redo(before)!;
        Assert.Equal(1, again.Shapes.Count);
    }

    [Test] static void MaterialTestGrid()
    {
        var p = MaterialTestGenerator.Create(new MachineProfile(), new MaterialTestOptions { SpeedSteps = 3, PowerSteps = 4 });
        Assert.Equal(12, p.Shapes.Count(s => s.Name.StartsWith("S")), "cells");
        Assert.Equal(13, p.Layers.Count, "12 cells + labels");
        var job = GcodeGenerator.Generate(p);
        Assert.True(job.Lines.Count > 100);
    }
}
