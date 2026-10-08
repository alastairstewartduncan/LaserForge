using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Gcode;

public sealed record GcodeJob(IReadOnlyList<string> Lines, Bounds Bounds, double EstimatedSeconds,
                              double CutDistanceMm, double TravelDistanceMm)
{
    public string Text => string.Join("\n", Lines) + "\n";
    public TimeSpan EstimatedTime => TimeSpan.FromSeconds(EstimatedSeconds);
}

/// <summary>Turns a <see cref="LaserProject"/> into GRBL G-code. Layers are burned in list order.</summary>
public static class GcodeGenerator
{
    public static GcodeJob Generate(LaserProject project, IEnumerable<Shape>? onlyThese = null)
    {
        var m = project.Machine;
        var w = new GcodeWriter(m);
        w.Header();
        var filter = onlyThese?.Select(s => s.Id).ToHashSet();

        foreach (var layer in project.Layers.Where(l => l.Output))
        {
            var shapes = project.ShapesOn(layer).Where(s => filter == null || filter.Contains(s.Id)).ToList();
            if (shapes.Count == 0) continue;

            double s = Math.Clamp(layer.PowerPercent, 0, 100) / 100.0 * m.MaxSpindleS;
            w.Comment($"Layer '{layer.Name}': {layer.Mode}, {layer.SpeedMmMin:0} mm/min, {layer.PowerPercent:0.#}% (S{s:0}), {layer.Passes} pass(es)");
            if (layer.AirAssist) w.Raw("M8");

            for (int pass = 0; pass < Math.Max(1, layer.Passes); pass++)
            {
                if (layer.Passes > 1) w.Comment($"Pass {pass + 1}/{layer.Passes}");
                switch (layer.Mode)
                {
                    case LayerMode.Line:
                        EmitLines(w, shapes.OfType<VectorShape>(), s, layer.SpeedMmMin);
                        break;
                    case LayerMode.Fill:
                        EmitFill(w, shapes.OfType<VectorShape>(), layer, s);
                        break;
                    case LayerMode.Image:
                        foreach (var img in shapes.OfType<ImageShape>().OrderBy(i => i.Y))
                            EmitRaster(w, img, layer, m);
                        // Vectors placed on an image layer are filled so nothing is silently dropped.
                        EmitFill(w, shapes.OfType<VectorShape>(), layer, s);
                        break;
                }
            }
            if (layer.AirAssist) w.Raw("M9");
        }

        w.Footer();
        var bounds = filter == null ? project.OutputBounds()
            : project.Shapes.Where(x => filter.Contains(x.Id)).Aggregate(Bounds.Empty, (b, x) => b.Union(x.GetBounds()));
        return new GcodeJob(w.Lines.ToList(), bounds, w.EstimatedSeconds, w.CutDistanceMm, w.TravelDistanceMm);
    }

    /// <summary>
    /// "Test sizing before burn": traces the job's bounding box so you can check placement on the material.
    /// With powerPercent = 0 the head moves with the laser off; a small value (1–2 %) gives a visible dot on most diodes.
    /// </summary>
    public static GcodeJob GenerateFrame(LaserProject project, double powerPercent = 0, double speedMmMin = 3000,
                                         Bounds? bounds = null)
    {
        var m = project.Machine;
        var b = bounds ?? project.OutputBounds();
        var w = new GcodeWriter(m);
        w.Comment("Frame / bounding-box trace");
        w.Raw("G21");
        w.Raw("G90");
        if (b.IsEmpty)
        {
            w.Comment("Nothing to frame");
            return new GcodeJob(w.Lines.ToList(), b, 0, 0, 0);
        }
        double s = Math.Clamp(powerPercent, 0, 100) / 100.0 * m.MaxSpindleS;
        w.Rapid(new Vec2(b.MinX, b.MinY));
        w.Raw(m.DynamicPower ? "M4 S0" : "M3 S0");
        w.Cut(new Vec2(b.MaxX, b.MinY), s, speedMmMin);
        w.Cut(new Vec2(b.MaxX, b.MaxY), s, speedMmMin);
        w.Cut(new Vec2(b.MinX, b.MaxY), s, speedMmMin);
        w.Cut(new Vec2(b.MinX, b.MinY), s, speedMmMin);
        w.Raw("M5");
        return new GcodeJob(w.Lines.ToList(), b, w.EstimatedSeconds, w.CutDistanceMm, w.TravelDistanceMm);
    }

    // ---------------------------------------------------------------- Line (outline / cut)

    private static void EmitLines(GcodeWriter w, IEnumerable<VectorShape> shapes, double s, double feed)
    {
        var paths = shapes.SelectMany(v => v.Paths).Where(p => p.Points.Count >= 2).ToList();
        foreach (var path in PathOrdering.Order(paths, w.Position ?? Vec2.Zero))
        {
            w.Rapid(path.Points[0]);
            for (int i = 1; i < path.Points.Count; i++) w.Cut(path.Points[i], s, feed);
            if (path.Closed) w.Cut(path.Points[0], s, feed);
        }
    }

    // ---------------------------------------------------------------- Fill (scan-line engraving)

    private static void EmitFill(GcodeWriter w, IEnumerable<VectorShape> shapes, Layer layer, double s)
    {
        // Each shape is filled on its own with the even-odd rule, so holes in letters (o, e, A…) stay open
        // and overlapping shapes are both burned.
        foreach (var shape in shapes.OrderBy(v => v.GetBounds().MinY).ThenBy(v => v.GetBounds().MinX))
        {
            var closed = shape.Paths.Where(p => p.Closed && p.Points.Count >= 3).ToList();
            if (closed.Count == 0) continue;
            var rows = ScanlineFill.Hatch(closed, layer.LineIntervalMm);
            bool leftToRight = true;
            foreach (var (y, segments) in rows)
            {
                if (segments.Count == 0) continue;
                int dir = leftToRight ? 1 : -1;
                var ordered = leftToRight ? segments : segments.Select(g => (g.X1, g.X0)).Reverse().ToList();
                double first = ordered[0].Item1, last = ordered[^1].Item2;

                w.Rapid(new Vec2(first - dir * layer.OverscanMm, y));
                w.Cut(new Vec2(first, y), 0, layer.SpeedMmMin);
                foreach (var (a, b) in ordered)
                {
                    w.Cut(new Vec2(a, y), 0, layer.SpeedMmMin);   // gap: laser off at speed
                    w.Cut(new Vec2(b, y), s, layer.SpeedMmMin);
                }
                w.Cut(new Vec2(last + dir * layer.OverscanMm, y), 0, layer.SpeedMmMin);
                if (layer.Bidirectional) leftToRight = !leftToRight;
            }
        }
    }

    // ---------------------------------------------------------------- Image (raster)

    private static void EmitRaster(GcodeWriter w, ImageShape img, Layer layer, MachineProfile m)
    {
        var (px, pw, ph) = ImageProcessor.Prepare(img, layer.LineIntervalMm);
        double pixelW = img.WidthMm / pw, pixelH = img.HeightMm / ph;
        double maxS = Math.Clamp(layer.PowerPercent, 0, 100) / 100.0 * m.MaxSpindleS;
        double minS = Math.Clamp(Math.Min(layer.MinPowerPercent, layer.PowerPercent), 0, 100) / 100.0 * m.MaxSpindleS;
        bool grayscale = img.Dither == DitherMode.Grayscale;
        double feed = layer.SpeedMmMin;

        double PowerOf(byte v)
        {
            if (v >= 255) return 0;
            if (!grayscale) return maxS;
            return Math.Round(minS + (maxS - minS) * (1 - v / 255.0));
        }

        w.Comment($"Image '{img.Name}' {pw}x{ph} px @ {layer.LineIntervalMm} mm, {img.Dither}");
        bool leftToRight = true;
        for (int row = 0; row < ph; row++)
        {
            int rowStart = row * pw;
            int first = -1, last = -1;
            for (int i = 0; i < pw; i++) if (px[rowStart + i] < 255) { first = i; break; }
            if (first < 0) continue; // blank row
            for (int i = pw - 1; i >= 0; i--) if (px[rowStart + i] < 255) { last = i; break; }

            double y = img.Y + (row + 0.5) * pixelH;
            int dir = leftToRight ? 1 : -1;
            double startX = leftToRight ? img.X + first * pixelW : img.X + (last + 1) * pixelW;
            double endX = leftToRight ? img.X + (last + 1) * pixelW : img.X + first * pixelW;

            w.Rapid(new Vec2(startX - dir * layer.OverscanMm, y));
            w.Cut(new Vec2(startX, y), 0, feed);

            // Run-length encode the row: one G1 per run of equal power.
            int iStart = leftToRight ? first : last;
            int iEnd = leftToRight ? last : first;
            double runPower = PowerOf(px[rowStart + iStart]);
            for (int i = iStart; ; i += dir)
            {
                bool atEnd = i == iEnd;
                double next = atEnd ? double.NaN : PowerOf(px[rowStart + i + dir]);
                if (atEnd || next != runPower)
                {
                    double x = leftToRight ? img.X + (i + 1) * pixelW : img.X + i * pixelW;
                    w.Cut(new Vec2(x, y), runPower, feed);
                    if (atEnd) break;
                    runPower = next;
                }
            }

            w.Cut(new Vec2(endX + dir * layer.OverscanMm, y), 0, feed);
            if (layer.Bidirectional) leftToRight = !leftToRight;
        }
    }
}
