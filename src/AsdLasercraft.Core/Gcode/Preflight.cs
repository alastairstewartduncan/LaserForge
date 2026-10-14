using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Gcode;

public enum PreflightSeverity
{
    /// <summary>Worth knowing; doesn't stop the job.</summary>
    Info,
    /// <summary>Probably a mistake; the user can burn anyway.</summary>
    Warning,
    /// <summary>The job can't run as it is.</summary>
    Error,
}

public sealed record PreflightIssue(PreflightSeverity Severity, string Message);

/// <summary>
/// Checks a project and its generated job for the mistakes that waste material: burning outside the bed,
/// layers that will silently do nothing, cuts that run before engraving, and so on.
/// </summary>
public static class Preflight
{
    public static List<PreflightIssue> Check(LaserProject project, GcodeJob job, IReadOnlyCollection<Guid>? onlyThese = null)
    {
        var issues = new List<PreflightIssue>();
        var m = project.Machine;
        bool Included(Shape s) => onlyThese == null || onlyThese.Contains(s.Id);

        if (job.Bounds.IsEmpty)
        {
            issues.Add(new(PreflightSeverity.Error, "Nothing to burn. Put shapes on a layer with Output ticked."));
            return issues;
        }

        // ---- placement
        var bed = Bounds.FromRect(0, 0, m.BedWidthMm, m.BedHeightMm);
        if (project.Job.Origin == JobOrigin.Absolute)
        {
            if (!bed.Contains(job.Bounds))
                issues.Add(new(PreflightSeverity.Warning, "Part of the design is outside the bed. The laser may hit its limits or stop with an alarm."));
            else if (job.Moves.Count > 0 && !InsideWithTolerance(bed, job.MotionBounds, 0.01))
                issues.Add(new(PreflightSeverity.Warning, "Overscan runs past the edge of the bed. Move the design inwards or reduce overscan."));
        }
        else
        {
            string where = project.Job.Origin == JobOrigin.CurrentPosition ? "the laser head's current position" : "your work origin (G54)";
            issues.Add(new(PreflightSeverity.Info,
                $"The {JobSettings.Describe(project.Job.Anchor)} of the job starts at {where}. Frame first to check it fits on the material."));
            if (job.Bounds.Width > m.BedWidthMm || job.Bounds.Height > m.BedHeightMm)
                issues.Add(new(PreflightSeverity.Warning, "The job is bigger than the bed."));
        }

        // ---- layers
        var outputLayers = project.Layers.Where(l => l.Output).ToList();
        foreach (var layer in outputLayers)
        {
            var shapes = project.ShapesOn(layer).Where(Included).ToList();
            if (shapes.Count == 0) continue;

            if (layer.PowerPercent <= 0)
                issues.Add(new(PreflightSeverity.Warning, $"Layer '{layer.Name}' has 0% power, so it won't mark anything."));

            if (!layer.Visible)
                issues.Add(new(PreflightSeverity.Warning, $"Layer '{layer.Name}' is hidden but will still burn. Untick Output to skip it."));

            if (layer.Mode is LayerMode.Fill or LayerMode.Image)
            {
                int open = shapes.OfType<VectorShape>().Count(v => !v.Paths.Any(p => p.Closed && p.Points.Count >= 3));
                if (open > 0)
                    issues.Add(new(PreflightSeverity.Warning,
                        $"{open} shape(s) on fill layer '{layer.Name}' have no closed outline and will be skipped. Put lines on a Line layer."));
            }

            if (layer.Mode == LayerMode.Image)
            {
                if (!m.DynamicPower && shapes.OfType<ImageShape>().Any(i => i.Dither == DitherMode.Grayscale))
                    issues.Add(new(PreflightSeverity.Warning,
                        $"Layer '{layer.Name}' uses grayscale with constant power (M3). Turn on dynamic power (M4) in Machine settings for even tones."));
                if (layer.LineIntervalMm < 0.05)
                    issues.Add(new(PreflightSeverity.Info, $"Layer '{layer.Name}' uses a very fine line interval ({layer.LineIntervalMm} mm). Most diode dots are 0.08–0.15 mm."));
            }

            if (layer.Mode == LayerMode.Line && layer.KerfMm > 0)
            {
                int openPaths = shapes.OfType<VectorShape>().SelectMany(v => v.Paths).Count(p => !p.Closed);
                if (openPaths > 0)
                    issues.Add(new(PreflightSeverity.Info, $"Kerf offset on layer '{layer.Name}' only applies to closed outlines; {openPaths} open line(s) are cut as drawn."));
            }

            if (layer.Mode != LayerMode.Line && layer.Bidirectional && m.ScanOffsets.Count == 0 && layer.SpeedMmMin >= 4000)
                issues.Add(new(PreflightSeverity.Info,
                    $"Layer '{layer.Name}' scans fast in both directions. If edges look fuzzy, set up scan offset in Machine settings."));
        }

        // A cut that runs before an engrave can let the part drop or shift before it's engraved.
        int firstCut = outputLayers.FindIndex(l => l.Mode == LayerMode.Line && IsCut(l) && project.ShapesOn(l).Any(Included));
        if (firstCut >= 0)
        {
            var laterEngrave = outputLayers.Skip(firstCut + 1)
                .FirstOrDefault(l => l.Mode != LayerMode.Line && project.ShapesOn(l).Any(Included));
            if (laterEngrave != null)
                issues.Add(new(PreflightSeverity.Warning,
                    $"Cut layer '{outputLayers[firstCut].Name}' burns before engrave layer '{laterEngrave.Name}'. Parts may drop out first — move cuts to the bottom of the list."));
        }

        if (job.EstimatedTime.TotalHours >= 1)
            issues.Add(new(PreflightSeverity.Info, $"Long job: about {job.EstimatedTime.TotalHours:0.0} hours. Never leave the laser unattended."));

        return issues;
    }

    /// <summary>Heuristic: a line layer is a cut (rather than a light score) if it is slow and powerful or multi-pass.</summary>
    public static bool IsCut(Layer l) => l.Passes > 1 || (l.PowerPercent >= 70 && l.SpeedMmMin <= 1000);

    private static bool InsideWithTolerance(Bounds outer, Bounds inner, double tol) =>
        inner.IsEmpty || (inner.MinX >= outer.MinX - tol && inner.MinY >= outer.MinY - tol &&
                          inner.MaxX <= outer.MaxX + tol && inner.MaxY <= outer.MaxY + tol);
}
