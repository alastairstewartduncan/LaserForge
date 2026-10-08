namespace LaserForge.Core.Model;

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

    public MachineProfile Clone() => (MachineProfile)MemberwiseClone();
}
