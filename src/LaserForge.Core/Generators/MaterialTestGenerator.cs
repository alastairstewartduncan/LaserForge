using System.Globalization;
using LaserForge.Core.Model;

namespace LaserForge.Core.Generators;

public sealed class MaterialTestOptions
{
    public double MinSpeed { get; set; } = 1000;
    public double MaxSpeed { get; set; } = 5000;
    public int SpeedSteps { get; set; } = 5;
    public double MinPower { get; set; } = 10;
    public double MaxPower { get; set; } = 100;
    public int PowerSteps { get; set; } = 5;
    public double CellSizeMm { get; set; } = 8;
    public double GapMm { get; set; } = 3;
    public LayerMode CellMode { get; set; } = LayerMode.Fill;
    public double LineIntervalMm { get; set; } = 0.1;
    public double OriginX { get; set; } = 10;
    public double OriginY { get; set; } = 10;
    public bool Labels { get; set; } = true;
    public double LabelPowerPercent { get; set; } = 20;
    public double LabelSpeed { get; set; } = 2000;
}

/// <summary>
/// "Test before you burn": builds a speed × power grid so you can find the right settings for a new material.
/// Power increases left→right, speed increases top→bottom. Each cell is its own layer.
/// </summary>
public static class MaterialTestGenerator
{
    public static LaserProject Create(MachineProfile machine, MaterialTestOptions o)
    {
        var p = new LaserProject { Machine = machine.Clone() };
        double labelH = Math.Max(2, o.CellSizeMm * 0.35);
        double left = o.OriginX + (o.Labels ? SegmentFont.MeasureWidth("00000", labelH) + o.GapMm : 0);
        double top = o.OriginY + (o.Labels ? labelH + o.GapMm : 0);

        Layer? labels = null;
        if (o.Labels)
        {
            labels = new Layer { Name = "Labels", ColorHex = "#555555", Mode = LayerMode.Line, SpeedMmMin = o.LabelSpeed, PowerPercent = o.LabelPowerPercent };
            p.Layers.Add(labels);
        }

        for (int r = 0; r < Math.Max(1, o.SpeedSteps); r++)
        {
            double speed = Step(o.MinSpeed, o.MaxSpeed, r, o.SpeedSteps);
            double y = top + r * (o.CellSizeMm + o.GapMm);
            for (int c = 0; c < Math.Max(1, o.PowerSteps); c++)
            {
                double power = Step(o.MinPower, o.MaxPower, c, o.PowerSteps);
                double x = left + c * (o.CellSizeMm + o.GapMm);
                var layer = new Layer
                {
                    Name = $"S{speed:0} P{power:0}",
                    ColorHex = HueColor((double)c / Math.Max(1, o.PowerSteps - 1)),
                    Mode = o.CellMode,
                    SpeedMmMin = Math.Round(speed),
                    PowerPercent = Math.Round(power, 1),
                    LineIntervalMm = o.LineIntervalMm,
                };
                p.Layers.Add(layer);
                var cell = ShapeFactory.Rectangle(x, y, o.CellSizeMm, o.CellSizeMm);
                cell.LayerId = layer.Id;
                cell.Name = layer.Name;
                p.Shapes.Add(cell);

                if (labels != null && r == 0)
                    AddLabel(p, labels, power.ToString("0", CultureInfo.InvariantCulture), x, o.OriginY, labelH);
            }
            if (labels != null)
                AddLabel(p, labels, speed.ToString("0", CultureInfo.InvariantCulture), o.OriginX, y + (o.CellSizeMm - labelH) / 2, labelH);
        }
        return p;
    }

    private static void AddLabel(LaserProject p, Layer layer, string text, double x, double y, double h)
    {
        var shape = new VectorShape(SegmentFont.Render(text, x, y, h), "Label " + text) { LayerId = layer.Id };
        p.Shapes.Add(shape);
    }

    private static double Step(double min, double max, int i, int steps) =>
        steps <= 1 ? min : min + (max - min) * i / (steps - 1);

    private static string HueColor(double t)
    {
        // Blue → red ramp so higher power reads as "hotter" on screen.
        int r = (int)(30 + 200 * t), g = (int)(90 - 40 * t), b = (int)(230 - 200 * t);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
