using LaserForge.Core.Geometry;

namespace LaserForge.Core.Model;

/// <summary>A document: layers (with burn settings), the shapes on them and the target machine.</summary>
public sealed class LaserProject
{
    public int FormatVersion { get; set; } = 1;
    public List<Layer> Layers { get; set; } = new();
    public List<Shape> Shapes { get; set; } = new();
    public MachineProfile Machine { get; set; } = new();

    public Layer? FindLayer(Guid id) => Layers.FirstOrDefault(l => l.Id == id);

    public IEnumerable<Shape> ShapesOn(Layer layer) => Shapes.Where(s => s.LayerId == layer.Id);

    /// <summary>Bounds of everything that will be burned (output layers only).</summary>
    public Bounds OutputBounds()
    {
        var b = Bounds.Empty;
        foreach (var layer in Layers.Where(l => l.Output))
            foreach (var s in ShapesOn(layer))
                b = b.Union(s.GetBounds());
        return b;
    }

    /// <summary>Returns the first layer with the given mode, creating it if missing.</summary>
    public Layer GetOrCreateLayer(LayerMode mode)
    {
        var existing = Layers.FirstOrDefault(l => l.Mode == mode);
        if (existing != null) return existing;
        var layer = DefaultLayer(mode);
        Layers.Add(layer);
        return layer;
    }

    public static Layer DefaultLayer(LayerMode mode) => mode switch
    {
        LayerMode.Fill => new Layer { Name = "Engrave", ColorHex = "#1E5AFF", Mode = LayerMode.Fill, SpeedMmMin = 3000, PowerPercent = 30, LineIntervalMm = 0.1 },
        LayerMode.Image => new Layer { Name = "Image", ColorHex = "#2EA043", Mode = LayerMode.Image, SpeedMmMin = 3000, PowerPercent = 40, LineIntervalMm = 0.1 },
        _ => new Layer { Name = "Cut", ColorHex = "#E5341C", Mode = LayerMode.Line, SpeedMmMin = 300, PowerPercent = 100, Passes = 2 },
    };

    public static LaserProject CreateDefault()
    {
        var p = new LaserProject();
        p.Layers.Add(DefaultLayer(LayerMode.Fill));
        p.Layers.Add(DefaultLayer(LayerMode.Image));
        p.Layers.Add(DefaultLayer(LayerMode.Line));
        return p;
    }
}
