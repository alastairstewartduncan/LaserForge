using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Model;

/// <summary>Where a job's coordinates are measured from.</summary>
public enum JobOrigin
{
    /// <summary>Positions on the bed map straight to machine coordinates (needs homing).</summary>
    Absolute,

    /// <summary>The anchor point of the job goes to the work origin you set with "Set work origin here" (G54).</summary>
    UserOrigin,

    /// <summary>The anchor point of the job goes wherever the laser head is when you press Start or Frame.</summary>
    CurrentPosition,
}

/// <summary>Which point of the job's bounding box is placed at the origin (screen orientation).</summary>
public enum JobAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
}

/// <summary>Per-project job placement settings.</summary>
public sealed class JobSettings
{
    public JobOrigin Origin { get; set; } = JobOrigin.Absolute;
    public JobAnchor Anchor { get; set; } = JobAnchor.BottomLeft;

    public JobSettings Clone() => (JobSettings)MemberwiseClone();

    /// <summary>The anchor point of <paramref name="b"/> in project space (Y-down).</summary>
    public static Vec2 AnchorPoint(Bounds b, JobAnchor anchor)
    {
        int col = (int)anchor % 3, row = (int)anchor / 3;
        double x = col switch { 0 => b.MinX, 1 => (b.MinX + b.MaxX) / 2, _ => b.MaxX };
        double y = row switch { 0 => b.MinY, 1 => (b.MinY + b.MaxY) / 2, _ => b.MaxY };
        return new Vec2(x, y);
    }

    public static string Describe(JobAnchor a) => a switch
    {
        JobAnchor.TopLeft => "top left",
        JobAnchor.TopCenter => "top centre",
        JobAnchor.TopRight => "top right",
        JobAnchor.MiddleLeft => "middle left",
        JobAnchor.Center => "centre",
        JobAnchor.MiddleRight => "middle right",
        JobAnchor.BottomLeft => "bottom left",
        JobAnchor.BottomCenter => "bottom centre",
        _ => "bottom right",
    };

    public static string Describe(JobOrigin o) => o switch
    {
        JobOrigin.UserOrigin => "User origin",
        JobOrigin.CurrentPosition => "Current position",
        _ => "Absolute coordinates",
    };
}
