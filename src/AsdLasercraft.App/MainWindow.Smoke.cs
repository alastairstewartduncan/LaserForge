using System.Windows.Media;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.App;

// Hooks used only by the CI smoke test (--smoke-test) to build a demo scene and render screenshots.
public partial class MainWindow
{
    /// <summary>A small but representative project: engraved text, a cut coaster with a hole, a star and a photo.</summary>
    internal void SmokeLoadDemo()
    {
        var p = LaserProject.CreateDefault();
        p.Machine = new MachineProfile { Name = "Demo 10 W diode", BedWidthMm = 400, BedHeightMm = 300 };
        var engrave = p.Layers.First(l => l.Mode == LayerMode.Fill);
        var image = p.Layers.First(l => l.Mode == LayerMode.Image);
        var cut = p.Layers.First(l => l.Mode == LayerMode.Line);
        cut.KerfMm = 0.15;
        cut.TabCount = 3;
        engrave.FillAngleDeg = 45;
        engrave.MaterialName = "Plywood (birch) 3 mm – Engrave";

        void Add(Shape s, Layer l) { s.LayerId = l.Id; p.Shapes.Add(s); }

        var coaster = ShapeFactory.Rectangle(40, 60, 110, 110, cornerRadius: 14);
        Add(coaster, cut);
        Add(ShapeFactory.Ellipse(85, 145, 20, 20), cut);
        var text = TextToPath.Build("A·S·D", new FontFamily("Segoe UI"), 30, bold: true, italic: false, new Vec2(52, 78));
        if (text.Count > 0) Add(new VectorShape(text, "Text \"A·S·D\""), engrave);
        Add(ShapeFactory.Polygon(58, 120, 26, 26, 5, 0.45), engrave);

        // A synthetic "photo": soft radial blobs and a sky gradient.
        int w = 240, h = 170;
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double sky = 230 - y * 0.6;
                double d1 = Math.Sqrt(Math.Pow(x - 80, 2) + Math.Pow(y - 70, 2));
                double d2 = Math.Sqrt(Math.Pow(x - 170, 2) + Math.Pow(y - 110, 2));
                double hill = y > 120 + 18 * Math.Sin(x / 22.0) ? 70 : 0;
                double v = sky - Math.Max(0, 120 - d1 * 2.2) - Math.Max(0, 90 - d2 * 1.6) - hill;
                gray[y * w + x] = (byte)Math.Clamp(v, 0, 255);
            }
        Add(new ImageShape { Name = "landscape.png", X = 190, Y = 60, WidthMm = 160, HeightMm = 113, PixelWidth = w, PixelHeight = h, Gray = gray, Dither = DitherMode.Jarvis }, image);

        _dirty = false;
        SetProject(p, null, fit: true);
        Surface.SelectOnly(new[] { p.Shapes.OfType<ImageShape>().First().Id });
    }

    /// <summary>Stages a "job running on the simulator" look for the Laser tab screenshot.</summary>
    internal void SmokeShowRunning()
    {
        RightTabs.SelectedIndex = 1;
        Surface.SelectOnly(Array.Empty<Guid>());
        var coaster = _project.Shapes.OfType<VectorShape>().First();
        var pts = coaster.Paths[0].Points;
        for (int i = 0; i < pts.Count * 2 / 3; i++) Surface.AddTrailPoint(pts[i], burning: true);
        Surface.LaserPosition = pts[pts.Count * 2 / 3];
        Surface.LaserFiring = true;
        ConnDot.Fill = (Brush)FindResource("Brush.Info");
        ConnDotLarge.Fill = ConnDot.Fill;
        ConnStateText.Text = "Simulator (no laser)";
        MachineText.Text = "Run  X104.20 Y161.85  · sim";
        SetGlyph(ConnectGlyph, "I.Unplug", "Disconnect");
        JobProgress.Maximum = 2940;
        JobProgress.Value = 1234;
        JobProgressText.Text = "1,234 / 2,940 lines  (42%)";
        JobEtaText.Text = "2:41 · ~3:42 left";
        StartButton.IsEnabled = false;
        PauseButton.IsEnabled = true;
        Log("Connected to the built-in simulator (4× speed)");
        Log("Grbl 1.1h ['$' for help]");
        Log("Starting job: 2,940 lines");
        Surface.InvalidateVisual();
    }

    internal void SmokeShowPreview(double fraction)
    {
        Surface.LaserFiring = false;
        Surface.LaserPosition = null;
        Surface.ClearTrail();
        RightTabs.SelectedIndex = 0;
        var job = GcodeGenerator.Generate(_project, new GcodeOptions { RecordMoves = true });
        StartSimulation(job, autoPlay: false);
        Surface.SimulationTime = job.EstimatedSeconds * fraction;
        UpdateSimTime();
        Surface.InvalidateVisual();
    }

    internal (GcodeJob Job, List<PreflightIssue> Issues) SmokeJob()
    {
        var job = GcodeGenerator.Generate(_project, new GcodeOptions { RecordMoves = true });
        return (job, Preflight.Check(_project, job));
    }

    internal Layer? SmokeActiveLayer => ActiveLayer;
}
