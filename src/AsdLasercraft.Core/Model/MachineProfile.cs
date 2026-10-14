namespace AsdLasercraft.Core.Model;

/// <summary>One row of the scan offset table: at this speed, bidirectional rows are this far out of line.</summary>
public sealed class ScanOffsetPoint
{
    public double SpeedMmMin { get; set; }

    /// <summary>
    /// Total misalignment between left-to-right and right-to-left rows at this speed, mm.
    /// Each direction is shifted back by half of this. Positive values fix the usual "laser lags behind" blur.
    /// </summary>
    public double OffsetMm { get; set; }

    public ScanOffsetPoint() { }
    public ScanOffsetPoint(double speedMmMin, double offsetMm) { SpeedMmMin = speedMmMin; OffsetMm = offsetMm; }
}

/// <summary>Settings for a GRBL diode laser.</summary>
public sealed class MachineProfile
{
    public string Name { get; set; } = "GRBL diode laser";

    public double BedWidthMm { get; set; } = 400;
    public double BedHeightMm { get; set; } = 400;

    /// <summary>GRBL $30 – the S value that means 100 % power. 1000 on most diode lasers, 255 on some.</summary>
    public double MaxSpindleS { get; set; } = 1000;

    public double TravelSpeedMmMin { get; set; } = 6000;

    /// <summary>
    /// Project space is Y-down (top-left origin, like the screen). Most diode lasers home to the front-left
    /// with Y increasing away from you, so the generator flips Y by default: machineY = BedHeight - y.
    /// </summary>
    public bool FlipY { get; set; } = true;

    /// <summary>Use M4 (dynamic power, scales with speed – best for diode lasers) instead of M3 (constant).</summary>
    public bool DynamicPower { get; set; } = true;

    public bool ReturnToOrigin { get; set; } = true;

    public int BaudRate { get; set; } = 115200;

    /// <summary>GRBL planner RX buffer size used for character-counting streaming.</summary>
    public int RxBufferSize { get; set; } = 128;

    /// <summary>
    /// Scan offset (backlash/lag) compensation for bidirectional fill and image engraving.
    /// Empty = off. Values between the listed speeds are interpolated; outside the range the nearest value is used.
    /// </summary>
    public List<ScanOffsetPoint> ScanOffsets { get; set; } = new();

    /// <summary>Scan offset to use at a given speed, mm (0 when no table is set).</summary>
    public double ScanOffsetAt(double speedMmMin)
    {
        var pts = ScanOffsets.Where(p => p.SpeedMmMin > 0).OrderBy(p => p.SpeedMmMin).ToList();
        if (pts.Count == 0) return 0;
        if (speedMmMin <= pts[0].SpeedMmMin) return pts[0].OffsetMm;
        if (speedMmMin >= pts[^1].SpeedMmMin) return pts[^1].OffsetMm;
        for (int i = 1; i < pts.Count; i++)
        {
            if (speedMmMin <= pts[i].SpeedMmMin)
            {
                var a = pts[i - 1];
                var b = pts[i];
                double t = (speedMmMin - a.SpeedMmMin) / (b.SpeedMmMin - a.SpeedMmMin);
                return a.OffsetMm + (b.OffsetMm - a.OffsetMm) * t;
            }
        }
        return pts[^1].OffsetMm;
    }

    /// <summary>Formats the scan offset table as "speed:offset, …" for display and editing.</summary>
    public string ScanOffsetsText() => string.Join(", ",
        ScanOffsets.OrderBy(p => p.SpeedMmMin).Select(p => FormattableString.Invariant($"{p.SpeedMmMin:0}:{p.OffsetMm:0.###}")));

    /// <summary>Parses "1000:0.05, 3000:0.15" (speed mm/min : offset mm). Returns false if any entry is malformed.</summary>
    public static bool TryParseScanOffsets(string text, out List<ScanOffsetPoint> result)
    {
        result = new List<ScanOffsetPoint>();
        foreach (var raw in text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(new[] { ':', '=' }, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offset) ||
                speed <= 0)
            {
                result.Clear();
                return false;
            }
            result.Add(new ScanOffsetPoint(speed, offset));
        }
        return true;
    }

    public MachineProfile Clone()
    {
        var c = (MachineProfile)MemberwiseClone();
        c.ScanOffsets = ScanOffsets.Select(p => new ScanOffsetPoint(p.SpeedMmMin, p.OffsetMm)).ToList();
        return c;
    }
}
