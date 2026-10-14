using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Generators;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Grbl;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Serialization;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.Core.Tests;

/// <summary>Shared builders for the v0.3 feature tests.</summary>
internal static class Build
{
    public static LaserProject Project(Shape s, LayerMode mode, Action<Layer>? configure = null) => Project(new[] { s }, mode, configure);

    public static LaserProject Project(IEnumerable<Shape> shapes, LayerMode mode, Action<Layer>? configure = null)
    {
        var p = new LaserProject();
        p.Machine.FlipY = false;
        p.Machine.ReturnToOrigin = false;
        var layer = LaserProject.DefaultLayer(mode);
        layer.Passes = 1;
        layer.OverscanMm = 0;
        configure?.Invoke(layer);
        p.Layers.Add(layer);
        foreach (var s in shapes)
        {
            s.LayerId = layer.Id;
            p.Shapes.Add(s);
        }
        return p;
    }

    public static GcodeJob Run(LaserProject p) => GcodeGenerator.Generate(p, new GcodeOptions { RecordMoves = true });

    public static List<JobMove> Burns(GcodeJob job) => job.Moves.Where(m => m.PowerFraction > 0).ToList();

    public static double BurnLength(GcodeJob job) => Burns(job).Sum(m => Vec2.Distance(m.From, m.To));

    public static VectorShape SquareWithHole() => new(new[]
    {
        ShapeFactory.Rectangle(0, 0, 10, 10).Paths[0],
        ShapeFactory.Rectangle(3, 3, 4, 4).Paths[0],
    });
}

internal static class ScanOffsetTests
{
    [Test] static void InterpolatesBetweenSpeeds()
    {
        var m = new MachineProfile { ScanOffsets = { new(1000, 0.1), new(3000, 0.3) } };
        Assert.Near(0.2, m.ScanOffsetAt(2000), 1e-9, "midpoint");
        Assert.Near(0.1, m.ScanOffsetAt(500), 1e-9, "below range clamps");
        Assert.Near(0.3, m.ScanOffsetAt(9000), 1e-9, "above range clamps");
        Assert.Near(0, new MachineProfile().ScanOffsetAt(2000), 1e-9, "no table = off");
    }

    [Test] static void ParsesTable()
    {
        Assert.True(MachineProfile.TryParseScanOffsets("1000:0.05, 3000:0.15", out var pts));
        Assert.Equal(2, pts.Count);
        Assert.Near(0.15, pts[1].OffsetMm, 1e-9);
        Assert.True(!MachineProfile.TryParseScanOffsets("fast:lots", out _), "rejects junk");
        Assert.True(MachineProfile.TryParseScanOffsets("", out var none) && none.Count == 0, "empty = off");
    }

    [Test] static void FillRowsShiftOppositeWays()
    {
        var p = Build.Project(ShapeFactory.Rectangle(0, 0, 10, 2), LayerMode.Fill, l => { l.LineIntervalMm = 1; l.SpeedMmMin = 2000; });
        p.Machine.ScanOffsets.Add(new ScanOffsetPoint(2000, 0.4));
        var burns = Build.Burns(Build.Run(p));
        Assert.Equal(2, burns.Count, "two rows");
        Assert.Near(-0.2, burns[0].From.X, 1e-9, "left-to-right row shifted back");
        Assert.Near(9.8, burns[0].To.X, 1e-9);
        Assert.Near(10.2, burns[1].From.X, 1e-9, "right-to-left row shifted the other way");
        Assert.Near(0.2, burns[1].To.X, 1e-9);
    }

    [Test] static void NoShiftWhenUnidirectional()
    {
        var p = Build.Project(ShapeFactory.Rectangle(0, 0, 10, 2), LayerMode.Fill, l => { l.LineIntervalMm = 1; l.Bidirectional = false; });
        p.Machine.ScanOffsets.Add(new ScanOffsetPoint(1000, 0.4));
        var burns = Build.Burns(Build.Run(p));
        Assert.True(burns.All(b => Math.Abs(b.From.X) < 1e-9 && Math.Abs(b.To.X - 10) < 1e-9), "rows untouched");
    }

    [Test] static void RasterRowsShift()
    {
        var img = new ImageShape { X = 0, Y = 0, WidthMm = 4, HeightMm = 2, PixelWidth = 4, PixelHeight = 2, Gray = new byte[8], Dither = DitherMode.Threshold };
        var p = Build.Project(img, LayerMode.Image, l => { l.LineIntervalMm = 1; l.SpeedMmMin = 3000; });
        p.Machine.ScanOffsets.Add(new ScanOffsetPoint(3000, 0.2));
        var burns = Build.Burns(Build.Run(p));
        Assert.Near(-0.1, burns[0].From.X, 1e-9, "row 1 start");
        Assert.Near(4.1, burns[1].From.X, 1e-9, "row 2 start (reverse)");
    }
}

internal static class FillAngleTests
{
    [Test] static void NinetyDegreesScansVertically()
    {
        var p = Build.Project(ShapeFactory.Rectangle(0, 0, 10, 10), LayerMode.Fill, l => { l.LineIntervalMm = 0.5; l.FillAngleDeg = 90; });
        var job = Build.Run(p);
        Assert.True(Build.Burns(job).All(b => Math.Abs(b.From.X - b.To.X) < 1e-9), "every burn is vertical");
        Assert.Near(200, Build.BurnLength(job), 1e-6, "area / interval");
    }

    [Test] static void DiagonalCoversArea()
    {
        var p = Build.Project(ShapeFactory.Rectangle(0, 0, 20, 20), LayerMode.Fill, l => { l.LineIntervalMm = 0.2; l.FillAngleDeg = 45; });
        var job = Build.Run(p);
        Assert.Near(400 / 0.2, Build.BurnLength(job), 400 / 0.2 * 0.02, "45° fill burns ~area / interval");
        var b = Build.Burns(job).First();
        Assert.Near(Math.Abs(b.To.X - b.From.X), Math.Abs(b.To.Y - b.From.Y), 1e-6, "lines run at 45°");
    }

    [Test] static void CrossHatchBurnsTwice()
    {
        var single = Build.Run(Build.Project(ShapeFactory.Rectangle(0, 0, 10, 10), LayerMode.Fill, l => l.LineIntervalMm = 0.5));
        var cross = Build.Run(Build.Project(ShapeFactory.Rectangle(0, 0, 10, 10), LayerMode.Fill, l => { l.LineIntervalMm = 0.5; l.CrossHatch = true; }));
        Assert.Near(2 * Build.BurnLength(single), Build.BurnLength(cross), 1e-6);
    }

    [Test] static void AngledFillKeepsHoles()
    {
        var p = Build.Project(Build.SquareWithHole(), LayerMode.Fill, l => { l.LineIntervalMm = 0.1; l.FillAngleDeg = 30; });
        var job = Build.Run(p);
        Assert.Near((100 - 16) / 0.1, Build.BurnLength(job), (100 - 16) / 0.1 * 0.02, "hole not burned");
        Assert.True(!Build.Burns(job).Any(b => Inside(Vec2.Lerp(b.From, b.To, 0.5), 3.2, 6.8)), "nothing burned inside the hole");
    }

    private static bool Inside(Vec2 p, double lo, double hi) => p.X > lo && p.X < hi && p.Y > lo && p.Y < hi;
}

internal static class KerfAndTabTests
{
    [Test] static void OffsetGrowsRegardlessOfWinding()
    {
        var cw = ShapeFactory.Rectangle(0, 0, 10, 10).Paths[0];
        var ccw = new Polyline(Enumerable.Reverse(cw.Points), true);
        foreach (var path in new[] { cw, ccw })
        {
            var b = PathOffset.Offset(path, 0.5).Bounds;
            Assert.Near(-0.5, b.MinX, 1e-9, "grows left"); Assert.Near(10.5, b.MaxX, 1e-9, "grows right");
            Assert.Near(-0.5, b.MinY, 1e-9); Assert.Near(10.5, b.MaxY, 1e-9);
            var s = PathOffset.Offset(path, -1).Bounds;
            Assert.Near(1, s.MinX, 1e-9, "shrinks"); Assert.Near(9, s.MaxX, 1e-9);
        }
    }

    [Test] static void KerfKeepsPartAndHoleSizes()
    {
        var p = Build.Project(Build.SquareWithHole(), LayerMode.Line, l => l.KerfMm = 0.2);
        var job = Build.Run(p);
        // Outer grows by 0.1 each side (10.2 square), hole shrinks by 0.1 each side (3.8 square).
        Assert.Near(4 * 10.2 + 4 * 3.8, job.CutDistanceMm, 1e-6);
    }

    [Test] static void TabsLeaveGapsOnPartsOnly()
    {
        var p = Build.Project(Build.SquareWithHole(), LayerMode.Line, l => { l.TabCount = 4; l.TabWidthMm = 1; });
        var job = Build.Run(p);
        Assert.Near(40 - 4 + 16, job.CutDistanceMm, 1e-6, "four 1 mm tabs on the outside, none in the hole");
    }

    [Test] static void TabWalkReturnsToStart()
    {
        var sq = ShapeFactory.Rectangle(0, 0, 10, 10).Paths[0];
        var walk = PathOffset.WalkWithTabs(sq, 2, 2);
        Assert.Equal(walk[0].Point, walk[^1].Point, "closed");
        double burned = 0, skipped = 0;
        for (int i = 1; i < walk.Count; i++)
        {
            double d = Vec2.Distance(walk[i - 1].Point, walk[i].Point);
            if (walk[i].Burn) burned += d; else skipped += d;
        }
        Assert.Near(36, burned, 1e-9); Assert.Near(4, skipped, 1e-9);
    }

    [Test] static void TooManyTabsAreIgnored()
    {
        var sq = ShapeFactory.Rectangle(0, 0, 4, 4).Paths[0]; // perimeter 16
        var walk = PathOffset.WalkWithTabs(sq, 10, 2);         // 20 mm of tabs > half the outline
        Assert.True(walk.Skip(1).All(w => w.Burn), "whole outline cut");
    }
}

internal static class OriginTests
{
    private static LaserProject Placed(JobOrigin origin, JobAnchor anchor)
    {
        var p = Build.Project(ShapeFactory.Rectangle(100, 100, 20, 10), LayerMode.Line);
        p.Machine.FlipY = true;
        p.Job.Origin = origin;
        p.Job.Anchor = anchor;
        return p;
    }

    [Test] static void CurrentPositionRelativeToAnchor()
    {
        var job = Build.Run(Placed(JobOrigin.CurrentPosition, JobAnchor.BottomLeft));
        Assert.True(job.Lines.Contains("G92 X0 Y0"), "head position becomes the anchor");
        Assert.True(job.Lines.Any(l => l.Contains("X20")) && job.Lines.Any(l => l.Contains("Y10")), "far corner at +20,+10 (Y up)");
        Assert.True(!job.Lines.Any(l => l.Contains("X100") || l.Contains("X120")), "no bed coordinates");
        int g921 = job.Lines.ToList().LastIndexOf("G92.1");
        Assert.True(g921 > 0 && g921 > job.Lines.ToList().IndexOf("M5"), "offset cleared at the end");
    }

    [Test] static void UserOriginCentreAnchor()
    {
        var job = Build.Run(Placed(JobOrigin.UserOrigin, JobAnchor.Center));
        Assert.True(job.Lines.Contains("G54") && !job.Lines.Contains("G92 X0 Y0"), "uses G54, no G92");
        Assert.True(job.Lines.Any(l => l.Contains("X-10")) && job.Lines.Any(l => l.Contains("X10")), "centred in X");
        Assert.True(job.Lines.Any(l => l.Contains("Y-5")) && job.Lines.Any(l => l.Contains("Y5")), "centred in Y");
    }

    [Test] static void AbsoluteUnchanged()
    {
        var job = Build.Run(Placed(JobOrigin.Absolute, JobAnchor.Center));
        Assert.True(job.Lines.Any(l => l.Contains("X120")), "bed coordinates");
        Assert.True(job.Lines.Contains("G92.1"), "clears stale temporary offsets");
    }

    [Test] static void FrameUsesSamePlacementAndEndsAtAnchor()
    {
        var p = Placed(JobOrigin.CurrentPosition, JobAnchor.TopRight);
        var frame = GcodeGenerator.GenerateFrame(p, 0, 3000, recordMoves: true);
        Assert.True(frame.Lines.Contains("G92 X0 Y0"));
        Assert.Equal(new Vec2(120, 100), frame.Moves[^1].To, "back at the top-right anchor");
        Assert.True(frame.Lines.Any(l => l.Contains("X-20")), "frame extends 20 mm left of the anchor");
    }

    [Test] static void AnchorPoints()
    {
        var b = Bounds.FromRect(0, 0, 10, 20);
        Assert.Equal(new Vec2(0, 0), JobSettings.AnchorPoint(b, JobAnchor.TopLeft));
        Assert.Equal(new Vec2(5, 10), JobSettings.AnchorPoint(b, JobAnchor.Center));
        Assert.Equal(new Vec2(10, 20), JobSettings.AnchorPoint(b, JobAnchor.BottomRight));
    }
}

internal static class PreviewAndPreflightTests
{
    [Test] static void MovesCoverWholeJob()
    {
        var p = Build.Project(ShapeFactory.Rectangle(10, 10, 20, 20), LayerMode.Fill, l => l.LineIntervalMm = 0.5);
        var job = Build.Run(p);
        Assert.True(job.Moves.Count > 40, "moves recorded");
        Assert.Near(job.EstimatedSeconds, job.Moves[^1].EndSeconds, 1e-6, "timeline ends at the estimate");
        for (int i = 1; i < job.Moves.Count; i++)
            Assert.True(job.Moves[i].StartSeconds >= job.Moves[i - 1].StartSeconds - 1e-9, "monotonic time");
        Assert.True(job.Moves.Where(m => m.PowerFraction > 0).All(m => m.LayerIndex == 0), "layer index stamped");
    }

    [Test] static void NotRecordedByDefault()
    {
        var job = GcodeGenerator.Generate(Build.Project(ShapeFactory.Rectangle(0, 0, 5, 5), LayerMode.Line));
        Assert.Equal(0, job.Moves.Count);
    }

    [Test] static void CleanJobHasNoWarnings()
    {
        var p = Build.Project(ShapeFactory.Rectangle(10, 10, 20, 20), LayerMode.Fill);
        var issues = Preflight.Check(p, Build.Run(p));
        Assert.True(issues.All(i => i.Severity == PreflightSeverity.Info), string.Join(" | ", issues.Select(i => i.Message)));
    }

    [Test] static void EmptyJobIsAnError()
    {
        var p = new LaserProject();
        p.Layers.Add(LaserProject.DefaultLayer(LayerMode.Fill));
        var issues = Preflight.Check(p, Build.Run(p));
        Assert.True(issues.Any(i => i.Severity == PreflightSeverity.Error));
    }

    [Test] static void FlagsOutsideBed()
    {
        var p = Build.Project(ShapeFactory.Rectangle(390, 10, 30, 10), LayerMode.Line);
        var issues = Preflight.Check(p, Build.Run(p));
        Assert.True(issues.Any(i => i.Severity == PreflightSeverity.Warning && i.Message.Contains("outside the bed")));
    }

    [Test] static void FlagsOverscanPastEdge()
    {
        var p = Build.Project(ShapeFactory.Rectangle(0.5, 10, 30, 10), LayerMode.Fill, l => l.OverscanMm = 3);
        var issues = Preflight.Check(p, Build.Run(p));
        Assert.True(issues.Any(i => i.Message.Contains("Overscan")), string.Join(" | ", issues.Select(i => i.Message)));
    }

    [Test] static void FlagsCutBeforeEngrave()
    {
        var p = new LaserProject();
        var cut = LaserProject.DefaultLayer(LayerMode.Line);
        var engrave = LaserProject.DefaultLayer(LayerMode.Fill);
        p.Layers.Add(cut); p.Layers.Add(engrave);
        var a = ShapeFactory.Rectangle(10, 10, 50, 50); a.LayerId = cut.Id; p.Shapes.Add(a);
        var b = ShapeFactory.Rectangle(20, 20, 10, 10); b.LayerId = engrave.Id; p.Shapes.Add(b);
        var issues = Preflight.Check(p, Build.Run(p));
        Assert.True(issues.Any(i => i.Message.Contains("before engrave")), string.Join(" | ", issues.Select(i => i.Message)));
    }

    [Test] static void FlagsOpenPathsOnFill()
    {
        var p = Build.Project(ShapeFactory.Line(new Vec2(10, 10), new Vec2(50, 50)), LayerMode.Fill);
        p.Shapes.Add(new VectorShape(new[] { ShapeFactory.Rectangle(60, 60, 5, 5).Paths[0] }) { LayerId = p.Layers[0].Id });
        var issues = Preflight.Check(p, Build.Run(p));
        Assert.True(issues.Any(i => i.Message.Contains("no closed outline")));
    }

    [Test] static void FlagsZeroPower()
    {
        var p = Build.Project(ShapeFactory.Rectangle(10, 10, 5, 5), LayerMode.Line, l => l.PowerPercent = 0);
        var issues = Preflight.Check(p, Build.Run(p));
        Assert.True(issues.Any(i => i.Message.Contains("0% power")));
    }
}

internal static class ToolTests
{
    [Test] static void GridArray()
    {
        var sq = ShapeFactory.Rectangle(0, 0, 10, 10);
        var copies = ArrayTool.Grid(new Shape[] { sq }, 3, 2, 5, 5);
        Assert.Equal(5, copies.Count, "3 × 2 minus the original");
        var all = ArrayTool.BoundsOf(copies.Append(sq));
        Assert.Near(40, all.Width, 1e-9); Assert.Near(25, all.Height, 1e-9);
        Assert.True(copies.Select(c => c.Id).Distinct().Count() == 5 && copies.All(c => c.Id != sq.Id), "fresh ids");
    }

    [Test] static void CircularArray()
    {
        var sq = ShapeFactory.Rectangle(45, -5, 10, 10); // centred on (50, 0)
        var copies = ArrayTool.Circular(new Shape[] { sq }, 4, Vec2.Zero, rotateCopies: true);
        Assert.Equal(3, copies.Count);
        var c = copies[0].GetBounds().Center;
        Assert.Near(0, c.X, 1e-6); Assert.Near(50, c.Y, 1e-6);
    }

    [Test] static void MaterialLibraryRoundTrips()
    {
        var lib = MaterialLibrary.CreateDefault();
        Assert.True(lib.Presets.Count >= 10, "ships with presets");
        var back = MaterialLibrary.FromJson(lib.ToJson());
        Assert.Equal(lib.Presets.Count, back.Presets.Count);
        Assert.Equal(lib.Presets[0].Mode, back.Presets[0].Mode);

        var layer = new Layer();
        var cut = lib.Presets.First(p => p.Operation == "Cut");
        cut.ApplyTo(layer);
        Assert.Equal(LayerMode.Line, layer.Mode);
        Assert.Equal(cut.Passes, layer.Passes);
        Assert.Equal(cut.DisplayName, layer.MaterialName);
    }

    [Test] static void TracesSquare()
    {
        var gray = Enumerable.Repeat((byte)255, 10 * 10).ToArray();
        for (int y = 3; y < 7; y++) for (int x = 3; x < 7; x++) gray[y * 10 + x] = 0;
        var img = new ImageShape { X = 0, Y = 0, WidthMm = 10, HeightMm = 10, PixelWidth = 10, PixelHeight = 10, Gray = gray };
        var paths = ImageTracer.Trace(img, new TraceOptions { MinAreaMm2 = 0.1 });
        Assert.Equal(1, paths.Count);
        var b = paths[0].Bounds;
        Assert.Near(3, b.MinX, 1e-9); Assert.Near(7, b.MaxX, 1e-9); Assert.Near(3, b.MinY, 1e-9); Assert.Near(7, b.MaxY, 1e-9);
        Assert.Equal(4, paths[0].Points.Count, "square simplifies to 4 corners");
    }

    [Test] static void TracesRingAsTwoLoops()
    {
        var gray = Enumerable.Repeat((byte)255, 12 * 12).ToArray();
        for (int y = 2; y < 10; y++) for (int x = 2; x < 10; x++) gray[y * 12 + x] = 0;
        for (int y = 5; y < 7; y++) for (int x = 5; x < 7; x++) gray[y * 12 + x] = 255;
        var img = new ImageShape { X = 0, Y = 0, WidthMm = 12, HeightMm = 12, PixelWidth = 12, PixelHeight = 12, Gray = gray };
        var paths = ImageTracer.Trace(img, new TraceOptions { MinAreaMm2 = 0.1 });
        Assert.Equal(2, paths.Count, "outline + hole");
        var rows = ScanlineFill.Hatch(paths, 1);
        var mid = rows.First(r => Math.Abs(r.Y - 5.5) < 1e-9);
        Assert.Equal(2, mid.Segments.Count, "the hole stays open when filled");
    }

    [Test] static void DiagonalPixelsStaySeparate()
    {
        var dark = new bool[4];
        dark[0] = true; dark[3] = true; // 2×2: top-left and bottom-right
        var loops = ImageTracer.TraceLoops(dark, 2, 2);
        Assert.Equal(2, loops.Count);
    }
}

internal static class SimulatorTests
{
    [Test] static void StreamsRasterWithoutOverflow()
    {
        var gray = new byte[40 * 30];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 37 % 256);
        var img = new ImageShape { X = 5, Y = 5, WidthMm = 20, HeightMm = 15, PixelWidth = 40, PixelHeight = 30, Gray = gray, Dither = DitherMode.Grayscale };
        var p = Build.Project(img, LayerMode.Image, l => l.LineIntervalMm = 0.5);
        p.Machine.ReturnToOrigin = true;
        var job = GcodeGenerator.Generate(p);

        var sim = new GrblSimulator(128);
        var streamer = new GrblStreamer(sim, 128);
        streamer.Start(job.Lines);
        int guard = 0;
        while (streamer.State == StreamState.Running && guard++ < 100_000) sim.ProcessAll();

        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.True(!sim.Overflowed, $"RX buffer overflowed (max {sim.MaxBufferedBytes} bytes)");
        Assert.True(sim.MaxBufferedBytes <= 127, "stays within 127 bytes");
        Assert.Equal(job.Lines.Select(GrblStreamer.Clean).Count(l => l.Length > 0), sim.ReceivedLines.Count, "every line delivered once");
        Assert.Equal(Vec2.Zero, sim.MachinePosition, "returned to origin");
    }

    [Test] static void InjectedErrorStopsJob()
    {
        var sim = new GrblSimulator { Responder = l => l.Contains("X3") ? "error:20" : null };
        var streamer = new GrblStreamer(sim);
        string? reported = null;
        streamer.ErrorReported += (line, err) => reported = line;
        streamer.Start(new[] { "G1 X1 F100", "G1 X2", "G1 X3", "G1 X4", "G1 X5" });
        sim.ProcessAll();
        Assert.Equal(StreamState.Faulted, streamer.State);
        Assert.Equal("G1 X3", reported);
        Assert.True(sim.RealtimeReceived.Contains('\x18'), "soft reset sent");
    }

    [Test] static void WorkOffsetsAndStatus()
    {
        var sim = new GrblSimulator();
        string? status = null;
        sim.LineReceived += l => { if (l.StartsWith('<')) status = l; };
        sim.Write("G0 X50 Y40\n");
        sim.Write("G92 X0 Y0\n");
        sim.Write("G0 X10 Y0\n");
        sim.ProcessAll();
        Assert.Equal(new Vec2(60, 40), sim.MachinePosition, "G92-relative move");
        sim.Write("?");
        Assert.True(GrblStatus.TryParse(status!, out var st), status ?? "no status");
        Assert.Equal(new Vec2(10, 0), st.WorkPos, "work position reported via WCO");
        sim.Write("G92.1\n");
        sim.ProcessAll();
        Assert.Equal(Vec2.Zero, sim.WorkOffset);
    }

    [Test] static void CurrentPositionJobLandsAtHead()
    {
        // Park the head at 200,150, run a current-position job, check it burned around the head.
        var p = Build.Project(ShapeFactory.Rectangle(10, 10, 20, 10), LayerMode.Line);
        p.Machine.FlipY = true;
        p.Job.Origin = JobOrigin.CurrentPosition;
        p.Job.Anchor = JobAnchor.BottomLeft;
        var job = GcodeGenerator.Generate(p);

        var sim = new GrblSimulator();
        var visited = new List<Vec2>();
        var streamer = new GrblStreamer(sim);
        sim.Write("G0 X200 Y150\n");
        sim.ProcessAll();
        streamer.Start(job.Lines);
        while (streamer.State == StreamState.Running)
        {
            sim.ProcessAll(1);
            visited.Add(sim.MachinePosition);
        }
        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.True(visited.Any(v => Vec2.Distance(v, new Vec2(220, 160)) < 1e-6), "opposite corner at +20,+10");
        Assert.Equal(new Vec2(200, 150), sim.MachinePosition, "back at the start");
        Assert.Equal(Vec2.Zero, sim.WorkOffset, "temporary offset cleared");
    }
}

internal static class FormatTests
{
    [Test] static void NewSettingsRoundTrip()
    {
        var p = LaserProject.CreateDefault();
        p.Job.Origin = JobOrigin.CurrentPosition;
        p.Job.Anchor = JobAnchor.Center;
        p.Machine.ScanOffsets.Add(new ScanOffsetPoint(3000, 0.12));
        p.Layers[0].FillAngleDeg = 45;
        p.Layers[0].CrossHatch = true;
        p.Layers[2].KerfMm = 0.15;
        p.Layers[2].TabCount = 3;
        var back = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p));
        Assert.Equal(JobOrigin.CurrentPosition, back.Job.Origin);
        Assert.Equal(JobAnchor.Center, back.Job.Anchor);
        Assert.Near(0.12, back.Machine.ScanOffsets[0].OffsetMm, 1e-9);
        Assert.True(back.Layers[0].CrossHatch && Math.Abs(back.Layers[0].FillAngleDeg - 45) < 1e-9);
        Assert.Equal(3, back.Layers[2].TabCount);
    }

    [Test] static void OldFilesStillLoad()
    {
        // A v0.2 file has no Job, ScanOffsets or new layer fields.
        var json = """{"FormatVersion":1,"Layers":[{"Id":"6f1c2a1e-0000-0000-0000-000000000001","Name":"Cut","ColorHex":"#E5341C","Mode":"Line","SpeedMmMin":300,"PowerPercent":100}],"Shapes":[],"Machine":{"Name":"Old"}}""";
        var p = ProjectSerializer.FromJson(json);
        Assert.Equal(JobOrigin.Absolute, p.Job.Origin);
        Assert.Equal(0, p.Machine.ScanOffsets.Count);
        Assert.Equal(0, p.Layers[0].TabCount);
    }

    [Test] static void MachineCloneIsDeep()
    {
        var m = new MachineProfile { ScanOffsets = { new(1000, 0.1) } };
        var c = m.Clone();
        c.ScanOffsets[0].OffsetMm = 0.5;
        Assert.Near(0.1, m.ScanOffsets[0].OffsetMm, 1e-9);
    }

    [Test] static void ScanOffsetCardBuilds()
    {
        var p = ScanOffsetTestGenerator.Create(new MachineProfile { ScanOffsets = { new(1000, 0.3) } }, new ScanOffsetTestOptions());
        Assert.Equal(0, p.Machine.ScanOffsets.Count, "calibrates with compensation off");
        Assert.Equal(6, p.Layers.Count, "labels + five speeds");
        Assert.True(GcodeGenerator.Generate(p).Lines.Count > 100);
    }
}
