namespace AsdLasercraft.Core.Model;

/// <summary>How a layer's shapes are burned.</summary>
public enum LayerMode
{
    /// <summary>Trace outlines. Use for cutting (slow, high power, several passes) or line scoring.</summary>
    Line,
    /// <summary>Scan-line fill inside closed shapes. Normal engraving of text and shapes.</summary>
    Fill,
    /// <summary>Raster engraving of bitmap images.</summary>
    Image,
}

/// <summary>
/// A cut/engrave layer. Every shape belongs to exactly one layer and is burned with that
/// layer's speed, power and mode. Layers are output top-to-bottom, so put cut layers last.
/// </summary>
public sealed class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";

    /// <summary>Display colour on the canvas, as #RRGGBB.</summary>
    public string ColorHex { get; set; } = "#000000";

    public LayerMode Mode { get; set; } = LayerMode.Line;

    /// <summary>Feed rate while burning, mm/min.</summary>
    public double SpeedMmMin { get; set; } = 1000;

    /// <summary>Laser power 0–100 % of the machine's max S value.</summary>
    public double PowerPercent { get; set; } = 50;

    /// <summary>Minimum power used for the lightest non-white grey in grayscale image mode.</summary>
    public double MinPowerPercent { get; set; } = 0;

    public int Passes { get; set; } = 1;

    /// <summary>Distance between scan lines for Fill and Image modes, mm. 0.1 mm ≈ 254 DPI.</summary>
    public double LineIntervalMm { get; set; } = 0.1;

    /// <summary>Scan in both directions (faster) or left-to-right only (cleaner on some machines).</summary>
    public bool Bidirectional { get; set; } = true;

    /// <summary>Extra distance travelled past each raster line end so the head is at speed while burning.</summary>
    public double OverscanMm { get; set; } = 2.5;

    /// <summary>Switch air assist on (M8) for this layer.</summary>
    public bool AirAssist { get; set; }

    public bool Output { get; set; } = true;
    public bool Visible { get; set; } = true;

    public Layer Clone() => (Layer)MemberwiseClone();

    public override string ToString() => $"{Name} ({Mode}, {SpeedMmMin:0} mm/min, {PowerPercent:0}%)";
}
