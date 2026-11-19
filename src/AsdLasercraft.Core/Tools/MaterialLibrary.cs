using System.Text.Json;
using System.Text.Json.Serialization;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Tools;

/// <summary>Saved speed/power settings for one job type on one material.</summary>
public sealed class MaterialPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Material { get; set; } = "Plywood";
    public double ThicknessMm { get; set; } = 3;
    /// <summary>Short description of what this preset does, e.g. "Cut", "Engrave", "Photo".</summary>
    public string Operation { get; set; } = "Engrave";
    public LayerMode Mode { get; set; } = LayerMode.Fill;
    public double SpeedMmMin { get; set; } = 3000;
    public double PowerPercent { get; set; } = 30;
    public int Passes { get; set; } = 1;
    public double LineIntervalMm { get; set; } = 0.1;
    public bool AirAssist { get; set; }
    public string Notes { get; set; } = "";

    public string DisplayName => ThicknessMm > 0
        ? $"{Material} {ThicknessMm:0.#} mm – {Operation}"
        : $"{Material} – {Operation}";

    public string Summary => $"{Mode}, {SpeedMmMin:0} mm/min, {PowerPercent:0.#}%" + (Passes > 1 ? $", {Passes} passes" : "");

    public MaterialPreset Clone()
    {
        var c = (MaterialPreset)MemberwiseClone();
        c.Id = Guid.NewGuid();
        return c;
    }

    /// <summary>Copies these settings onto a layer (keeps its name, colour and shapes).</summary>
    public void ApplyTo(Layer layer)
    {
        layer.Mode = Mode;
        layer.SpeedMmMin = SpeedMmMin;
        layer.PowerPercent = PowerPercent;
        layer.Passes = Math.Max(1, Passes);
        layer.LineIntervalMm = LineIntervalMm;
        layer.AirAssist = AirAssist;
        layer.MaterialName = DisplayName;
    }

    public static MaterialPreset FromLayer(Layer layer, string material, double thicknessMm, string operation) => new()
    {
        Material = material,
        ThicknessMm = thicknessMm,
        Operation = operation,
        Mode = layer.Mode,
        SpeedMmMin = layer.SpeedMmMin,
        PowerPercent = layer.PowerPercent,
        Passes = layer.Passes,
        LineIntervalMm = layer.LineIntervalMm,
        AirAssist = layer.AirAssist,
    };
}

/// <summary>
/// The user's material library, stored as JSON. Ships with starting points for a typical 5–10 W diode laser;
/// every machine differs, so the defaults are deliberately conservative and should be checked with a test grid.
/// </summary>
public sealed class MaterialLibrary
{
    public int FormatVersion { get; set; } = 1;
    public List<MaterialPreset> Presets { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MaterialLibrary Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var lib = JsonSerializer.Deserialize<MaterialLibrary>(File.ReadAllText(path), Options);
                if (lib != null) return lib;
            }
        }
        catch (Exception)
        {
            // A corrupt library shouldn't stop the app; fall back to the defaults and keep the bad file.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
        }
        return CreateDefault();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static MaterialLibrary FromJson(string json) =>
        JsonSerializer.Deserialize<MaterialLibrary>(json, Options) ?? new MaterialLibrary();

    public IEnumerable<string> Materials => Presets.Select(p => p.Material).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s);

    public static MaterialLibrary CreateDefault()
    {
        const string note = "Starting point for a 5–10 W diode. Run a material test grid first.";
        MaterialPreset P(string material, double t, string op, LayerMode mode, double speed, double power, int passes = 1,
                         double interval = 0.1, bool air = false) =>
            new() { Material = material, ThicknessMm = t, Operation = op, Mode = mode, SpeedMmMin = speed, PowerPercent = power,
                    Passes = passes, LineIntervalMm = interval, AirAssist = air, Notes = note };

        return new MaterialLibrary
        {
            Presets =
            {
                P("Plywood (birch)", 3, "Cut", LayerMode.Line, 300, 100, 2, air: true),
                P("Plywood (birch)", 3, "Engrave", LayerMode.Fill, 3000, 35),
                P("Plywood (birch)", 3, "Photo", LayerMode.Image, 3000, 40, interval: 0.1),
                P("Plywood (birch)", 3, "Score line", LayerMode.Line, 2000, 40),
                P("MDF", 3, "Cut", LayerMode.Line, 250, 100, 3, air: true),
                P("MDF", 3, "Engrave", LayerMode.Fill, 3000, 40),
                P("Basswood", 3, "Cut", LayerMode.Line, 400, 100, 2, air: true),
                P("Leather (veg-tan)", 2, "Engrave", LayerMode.Fill, 4000, 25),
                P("Cardboard", 2, "Cut", LayerMode.Line, 800, 70, 1, air: true),
                P("Paper / card", 0.3, "Cut", LayerMode.Line, 1500, 40),
                P("Slate", 0, "Photo", LayerMode.Image, 2000, 60, interval: 0.1),
                P("Anodised aluminium", 0, "Engrave", LayerMode.Fill, 2000, 80, interval: 0.08),
                P("Acrylic (black, cast)", 3, "Engrave", LayerMode.Fill, 2500, 50),
                P("Cork", 3, "Engrave", LayerMode.Fill, 4000, 20),
            },
        };
    }
}
