using System.Globalization;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Generators;

public sealed class ScanOffsetTestOptions
{
    public double[] Speeds { get; set; } = { 1000, 2000, 3000, 4500, 6000 };
    public double PowerPercent { get; set; } = 30;
    public double LineIntervalMm { get; set; } = 0.1;
    public double OriginX { get; set; } = 10;
    public double OriginY { get; set; } = 10;
}

/// <summary>
/// Builds a scan offset calibration card: for each speed, a row of thin bars filled bidirectionally with
/// scan offset switched off. Look at the bar edges under a magnifier: if they are fuzzy or doubled, measure
/// the width of the blur and enter it for that speed in Machine settings → Scan offsets.
/// </summary>
public static class ScanOffsetTestGenerator
{
    public static LaserProject Create(MachineProfile machine, ScanOffsetTestOptions o)
    {
        var m = machine.Clone();
        m.ScanOffsets.Clear(); // calibrate with compensation off
        var p = new LaserProject { Machine = m };
        const double barW = 1.0, barH = 8, gap = 1.5, labelH = 3;
        var labels = new Layer { Name = "Labels", ColorHex = "#8A93A6", Mode = LayerMode.Line, SpeedMmMin = 2000, PowerPercent = 20 };
        p.Layers.Add(labels);

        double y = o.OriginY;
        foreach (var speed in o.Speeds.Where(v => v > 0))
        {
            var layer = new Layer
            {
                Name = $"Scan {speed:0} mm/min",
                ColorHex = "#FF7A1A",
                Mode = LayerMode.Fill,
                SpeedMmMin = speed,
                PowerPercent = o.PowerPercent,
                LineIntervalMm = o.LineIntervalMm,
                Bidirectional = true,
            };
            p.Layers.Add(layer);
            double labelW = SegmentFont.MeasureWidth("00000", labelH);
            var label = new VectorShape(SegmentFont.Render(speed.ToString("0", CultureInfo.InvariantCulture), o.OriginX, y + (barH - labelH) / 2, labelH),
                                        "Label " + speed) { LayerId = labels.Id };
            p.Shapes.Add(label);
            double x = o.OriginX + labelW + 4;
            for (int i = 0; i < 6; i++)
            {
                var bar = ShapeFactory.Rectangle(x + i * (barW + gap), y, barW, barH);
                bar.LayerId = layer.Id;
                bar.Name = layer.Name;
                p.Shapes.Add(bar);
            }
            y += barH + 4;
        }
        return p;
    }
}
