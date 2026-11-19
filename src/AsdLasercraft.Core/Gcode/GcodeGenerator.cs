using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Gcode;

public sealed record GcodeJob(IReadOnlyList<string> Lines, Bounds Bounds, double EstimatedSeconds,
                              double CutDistanceMm, double TravelDistanceMm)
{
    public string Text => string.Join("\n", Lines) + "\n";
    public TimeSpan EstimatedTime => TimeSpan.FromSeconds(EstimatedSeconds);

    /// <summary>Every head movement in project space, for the simulated preview (empty unless requested).</summary>
    public IReadOnlyList<JobMove> Moves { get; init; } = Array.Empty<JobMove>();

    /// <summary>How the job is placed on the machine (absolute, user origin or current position + anchor).</summary>
    public JobPlacement Placement { get; init; } = JobPlacement.Absolute;

    /// <summary>Extent of every head movement including overscan and travel, in project space.</summary>
    public Bounds MotionBounds
    {
        get
        {
            var b = Bounds.Empty;
            foreach (var m in Moves) b = b.Include(m.From).Include(m.To);
            return b;
        }
    }
}

/// <summary>Options that change how a job is generated without changing the project.</summary>
public sealed class GcodeOptions
{
    /// <summary>Only burn these shapes (null = everything on output layers).</summary>
    public IEnumerable<Shape>? OnlyThese { get; init; }

    /// <summary>Record every move for the simulated preview. Costs memory on big rasters.</summary>
    public bool RecordMoves { get; init; }
}

/// <summary>Turns a <see cref="LaserProject"/> into GRBL G-code. Layers are burned in list order.</summary>
public static class GcodeGenerator
{
    public static GcodeJob Generate(LaserProject project, IEnumerable<Shape>? onlyThese = null) =>
        Generate(project, new GcodeOptions { OnlyThese = onlyThese });

    public static GcodeJob Generate(LaserProject project, GcodeOptions options)
    {
        var m = project.Machine;
        var filter = options.OnlyThese?.Select(s => s.Id).ToHashSet();
        var bounds = filter == null ? project.OutputBounds()
            : project.Shapes.Where(x => filter.Contains(x.Id) && (project.FindLayer(x.LayerId)?.Output ?? false))
                .Aggregate(Bounds.Empty, (b, x) => b.Union(x.GetBounds()));
        var placement = PlacementFor(project, bounds);

        var w = new GcodeWriter(m, placement, options.RecordMoves);
        w.Header();

        for (int li = 0; li < project.Layers.Count; li++)
        {
            var layer = project.Layers[li];
            if (!layer.Output) continue;
            var shapes = project.ShapesOn(layer).Where(s => filter == null || filter.Contains(s.Id)).ToList();
            if (shapes.Count == 0) continue;

            w.CurrentLayerIndex = li;
            double s = Math.Clamp(layer.PowerPercent, 0, 100) / 100.0 * m.MaxSpindleS;
            double scanOffset = layer.Bidirectional ? m.ScanOffsetAt(layer.SpeedMmMin) : 0;
            w.Comment($"Layer '{layer.Name}': {layer.Mode}, {layer.SpeedMmMin:0} mm/min, {layer.PowerPercent:0.#}% (S{s:0}), {layer.Passes} pass(es)");
            if (Math.Abs(scanOffset) > 1e-9) w.Comment(FormattableString.Invariant($"Scan offset {scanOffset:0.###} mm"));
            if (layer.AirAssist) w.Raw("M8");

            // Line-layer geometry (kerf offset) is the same every pass, so build it once.
            List<Polyline>? cutPaths = layer.Mode == LayerMode.Line ? PrepareCutPaths(shapes.OfType<VectorShape>(), layer) : null;

            for (int pass = 0; pass < Math.Max(1, layer.Passes); pass++)
            {
                if (layer.Passes > 1) w.Comment($"Pass {pass + 1}/{layer.Passes}");
                switch (layer.Mode)
                {
                    case LayerMode.Line:
                        EmitLines(w, cutPaths!, layer, s);
                        break;
                    case LayerMode.Fill:
                        EmitFill(w, shapes.OfType<VectorShape>(), layer, s, scanOffset);
                        break;
                    case LayerMode.Image:
                        foreach (var img in shapes.OfType<ImageShape>().OrderBy(i => i.Y))
                            EmitRaster(w, img, layer, m, scanOffset);
                        // Vectors placed on an image layer are filled so nothing is silently dropped.
                        EmitFill(w, shapes.OfType<VectorShape>(), layer, s, scanOffset);
                        break;
                }
            }
            if (layer.AirAssist) w.Raw("M9");
        }

        w.CurrentLayerIndex = -1;
        w.Footer();
        return new GcodeJob(w.Lines.ToList(), bounds, w.EstimatedSeconds, w.CutDistanceMm, w.TravelDistanceMm)
        {
            Moves = w.Moves,
            Placement = placement,
        };
    }

    /// <summary>Where the job's anchor point is, for the project's origin setting and the given job bounds.</summary>
    public static JobPlacement PlacementFor(LaserProject project, Bounds jobBounds)
    {
        var job = project.Job ?? new JobSettings();
        if (job.Origin == JobOrigin.Absolute || jobBounds.IsEmpty) return new JobPlacement(job.Origin, Vec2.Zero);
        return new JobPlacement(job.Origin, JobSettings.AnchorPoint(jobBounds, job.Anchor));
    }

    /// <summary>
    /// "Test sizing before burn": traces the job's bounding box so you can check placement on the material.
    /// With powerPercent = 0 the head moves with the laser off; a small value (1–2 %) gives a visible dot on most diodes.
    /// Uses the same origin and anchor as the job, so the frame lands exactly where the burn will.
    /// </summary>
    public static GcodeJob GenerateFrame(LaserProject project, double powerPercent = 0, double speedMmMin = 3000,
                                         Bounds? bounds = null, bool recordMoves = false)
    {
        var m = project.Machine;
        var b = bounds ?? project.OutputBounds();
        var placement = PlacementFor(project, b);
        var w = new GcodeWriter(m, placement, recordMoves);
        w.Comment("Frame / bounding-box trace");
        if (b.IsEmpty)
        {
            w.Comment("Nothing to frame");
            return new GcodeJob(w.Lines.ToList(), b, 0, 0, 0) { Placement = placement };
        }
        w.Preamble();
        double s = Math.Clamp(powerPercent, 0, 100) / 100.0 * m.MaxSpindleS;
        w.Rapid(new Vec2(b.MinX, b.MinY));
        w.Raw(m.DynamicPower ? "M4 S0" : "M3 S0");
        w.Cut(new Vec2(b.MaxX, b.MinY), s, speedMmMin);
        w.Cut(new Vec2(b.MaxX, b.MaxY), s, speedMmMin);
        w.Cut(new Vec2(b.MinX, b.MaxY), s, speedMmMin);
        w.Cut(new Vec2(b.MinX, b.MinY), s, speedMmMin);
        w.Raw("M5");
        if (placement.IsRelative)
        {
            w.Rapid(placement.Anchor); // finish where the job will start
            if (placement.Origin == JobOrigin.CurrentPosition) w.Raw("G92.1");
        }
        return new GcodeJob(w.Lines.ToList(), b, w.EstimatedSeconds, w.CutDistanceMm, w.TravelDistanceMm)
        {
            Moves = w.Moves,
            Placement = placement,
        };
    }

    // ---------------------------------------------------------------- Line (outline / cut)

    /// <summary>
    /// Applies kerf offset to closed outlines: outer outlines (parts) grow by half the kerf, holes shrink by half,
    /// so the finished part and its holes come out at the drawn size.
    /// </summary>
    private static List<Polyline> PrepareCutPaths(IEnumerable<VectorShape> shapes, Layer layer)
    {
        var paths = shapes.SelectMany(v => v.Paths).Where(p => p.Points.Count >= 2).ToList();
        double half = Math.Max(0, layer.KerfMm) / 2;
        if (half <= 1e-9) return paths;
        var depths = PathOrdering.NestingDepths(paths);
        var result = new List<Polyline>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            result.Add(p.Closed ? PathOffset.Offset(p, depths[i] % 2 == 0 ? half : -half) : p);
        }
        return result;
    }

    private static void EmitLines(GcodeWriter w, List<Polyline> paths, Layer layer, double s)
    {
        double feed = layer.SpeedMmMin;
        var ordered = PathOrdering.Order(paths, w.Position ?? Vec2.Zero);
        // Tabs go on part outlines only (even nesting depth), never on holes.
        var depths = layer.TabCount > 0 ? PathOrdering.NestingDepths(ordered) : null;

        for (int k = 0; k < ordered.Count; k++)
        {
            var path = ordered[k];
            w.Rapid(path.Points[0]);
            if (depths != null && path.Closed && depths[k] % 2 == 0)
            {
                foreach (var (pt, burn) in PathOffset.WalkWithTabs(path, layer.TabCount, layer.TabWidthMm).Skip(1))
                    w.Cut(pt, burn ? s : 0, feed);
                continue;
            }
            for (int i = 1; i < path.Points.Count; i++) w.Cut(path.Points[i], s, feed);
            if (path.Closed) w.Cut(path.Points[0], s, feed);
        }
    }

    // ---------------------------------------------------------------- Fill (scan-line engraving)

    private static void EmitFill(GcodeWriter w, IEnumerable<VectorShape> shapes, Layer layer, double s, double scanOffset)
    {
        // Each shape is filled on its own with the even-odd rule, so holes in letters (o, e, A…) stay open
        // and overlapping shapes are both burned.
        var angles = layer.CrossHatch ? new[] { layer.FillAngleDeg, layer.FillAngleDeg + 90 } : new[] { layer.FillAngleDeg };
        foreach (var shape in shapes.OrderBy(v => v.GetBounds().MinY).ThenBy(v => v.GetBounds().MinX))
        {
            var closed = shape.Paths.Where(p => p.Closed && p.Points.Count >= 3).ToList();
            if (closed.Count == 0) continue;
            foreach (var angle in angles) EmitHatch(w, closed, angle, layer, s, scanOffset);
        }
    }

    /// <summary>
    /// Hatches closed paths along <paramref name="angleDeg"/>: rotate the geometry so the scan direction is
    /// horizontal, hatch, and rotate every point back. Bidirectional rows are each shifted back by half the
    /// scan offset so the two directions line up.
    /// </summary>
    private static void EmitHatch(GcodeWriter w, List<Polyline> closed, double angleDeg, Layer layer, double s, double scanOffset)
    {
        double norm = ((angleDeg % 360) + 360) % 360;
        bool rotated = Math.Abs(norm) > 1e-9;
        var toLocal = Transform2D.Rotate(-angleDeg);
        var toWorld = Transform2D.Rotate(angleDeg);
        var local = rotated ? closed.Select(p => p.Transformed(toLocal)).ToList() : closed;
        var rows = ScanlineFill.Hatch(local, layer.LineIntervalMm);
        double half = layer.Bidirectional ? scanOffset / 2 : 0;
        double feed = layer.SpeedMmMin;
        Vec2 P(double x, double y) => rotated ? toWorld.Apply(new Vec2(x, y)) : new Vec2(x, y);

        bool leftToRight = true;
        foreach (var (y, segments) in rows)
        {
            if (segments.Count == 0) continue;
            int dir = leftToRight ? 1 : -1;
            double shift = -dir * half;
            var ordered = leftToRight ? segments : segments.Select(g => (g.X1, g.X0)).Reverse().ToList();
            double first = ordered[0].Item1 + shift, last = ordered[^1].Item2 + shift;

            w.Rapid(P(first - dir * layer.OverscanMm, y));
            w.Cut(P(first, y), 0, feed);
            foreach (var (a, b) in ordered)
            {
                w.Cut(P(a + shift, y), 0, feed);   // gap: laser off at speed
                w.Cut(P(b + shift, y), s, feed);
            }
            w.Cut(P(last + dir * layer.OverscanMm, y), 0, feed);
            if (layer.Bidirectional) leftToRight = !leftToRight;
        }
    }

    // ---------------------------------------------------------------- Image (raster)

    private static void EmitRaster(GcodeWriter w, ImageShape img, Layer layer, MachineProfile m, double scanOffset)
    {
        var (px, pw, ph) = ImageProcessor.Prepare(img, layer.LineIntervalMm);
        double pixelW = img.WidthMm / pw, pixelH = img.HeightMm / ph;
        double maxS = Math.Clamp(layer.PowerPercent, 0, 100) / 100.0 * m.MaxSpindleS;
        double minS = Math.Clamp(Math.Min(layer.MinPowerPercent, layer.PowerPercent), 0, 100) / 100.0 * m.MaxSpindleS;
        bool grayscale = img.Dither == DitherMode.Grayscale;
        double feed = layer.SpeedMmMin;
        double half = layer.Bidirectional ? scanOffset / 2 : 0;

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
            double shift = -dir * half;
            double startX = (leftToRight ? img.X + first * pixelW : img.X + (last + 1) * pixelW) + shift;
            double endX = (leftToRight ? img.X + (last + 1) * pixelW : img.X + first * pixelW) + shift;

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
                    double x = (leftToRight ? img.X + (i + 1) * pixelW : img.X + i * pixelW) + shift;
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
