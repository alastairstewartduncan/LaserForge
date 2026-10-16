using System.Globalization;
using AsdLasercraft.Core.Generators;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.Core.Ai;

/// <summary>What the user wants the test grid to tell them.</summary>
public enum GridGoal
{
    /// <summary>Darkest clean engraving without charring, burning through or a heavy halo.</summary>
    Engrave,
    /// <summary>Lightest setting that still gives clear, even contrast (fast and gentle).</summary>
    LightEngrave,
    /// <summary>Best for photos: smooth, mid-dark tone with visible detail, not saturated black.</summary>
    Photo,
    /// <summary>Cells (Line mode) that cut cleanly all the way through.</summary>
    Cut,
}

public sealed class GridReading
{
    /// <summary>Zero-based; row 0 = slowest speed (top), column 0 = lowest power (left).</summary>
    public int Row { get; init; }
    public int Column { get; init; }
    public double SpeedMmMin { get; init; }
    public double PowerPercent { get; init; }
    /// <summary>0–1, how sure the model is.</summary>
    public double Confidence { get; init; }
    public string Reasoning { get; init; } = "";
    /// <summary>A second-best cell, if the model suggested one.</summary>
    public (int Row, int Column)? Alternative { get; init; }
    /// <summary>Practical tips, e.g. "use air assist", "clean the lens".</summary>
    public string Advice { get; init; } = "";

    public string Summary => $"{SpeedMmMin:0} mm/min at {PowerPercent:0.#}% power";
}

/// <summary>
/// Reads a photo of a burned material test grid (from Tools → Material test grid) and picks the best cell for the
/// chosen goal. The grid's layout is known, so the model only has to judge the burns; the speed and power then come
/// from the grid settings rather than from anything the model reads off the photo.
/// </summary>
public sealed class TestGridReader
{
    private readonly IAiClient _client;

    public TestGridReader(IAiClient client) { _client = client; }

    public async Task<GridReading> ReadAsync(byte[] photo, string mediaType, MaterialTestOptions grid, GridGoal goal,
                                             string material, CancellationToken ct = default)
    {
        var response = await _client.SendAsync(new AiRequest
        {
            Purpose = "test-grid",
            System = SystemPrompt,
            MaxTokens = 1200,
            Messages =
            {
                AiMessage.User(new AiImage(mediaType, photo), new AiText(BuildBrief(grid, goal, material))),
            },
        }, ct).ConfigureAwait(false);
        return Parse(response.Text, grid);
    }

    public const string SystemPrompt = """
        You are a laser engraving expert inside A·S·D Lasercraft. You look at photos of burned material test grids
        and choose the best speed/power cell for the user's goal. Judge only what you can see: darkness and evenness of
        the burn, charring, scorch halos, melting, burn-through, and for cut tests whether the piece is cut cleanly through
        (light visible through the kerf, or a dropped/loose piece). If the photo is unclear, say so and lower your confidence.
        Reply ONLY with a JSON object, no other text.
        """;

    public static string BuildBrief(MaterialTestOptions g, GridGoal goal, string material)
    {
        var speeds = Enumerable.Range(0, Math.Max(1, g.SpeedSteps)).Select(r => Fmt(Speed(g, r)));
        var powers = Enumerable.Range(0, Math.Max(1, g.PowerSteps)).Select(c => Fmt(Power(g, c)));
        string goalText = goal switch
        {
            GridGoal.LightEngrave => "the LIGHTEST cell that still has clear, even contrast (gentle and fast)",
            GridGoal.Photo => "the best cell for photo engraving: smooth, even mid-to-dark tone, not saturated black, no charring",
            GridGoal.Cut => "the FASTEST / LOWEST-power cell that is still cleanly cut all the way through",
            _ => "the darkest clean engraving: deep, even colour without charring, burn-through or a heavy scorch halo",
        };
        return $$"""
            Material: {{(string.IsNullOrWhiteSpace(material) ? "not specified" : material.Trim())}}
            Grid: {{g.SpeedSteps}} rows × {{g.PowerSteps}} columns of {{g.CellSizeMm:0.#}} mm squares ({{(g.CellMode == LayerMode.Line ? "outlines (cut test)" : "filled squares")}}).
            Rows top→bottom are speeds in mm/min: {{string.Join(", ", speeds)}}.
            Columns left→right are power %: {{string.Join(", ", powers)}}.
            {{(g.Labels ? "Speed labels are engraved left of each row and power labels above each column." : "There are no labels.")}}
            If the photo is rotated, use the labels or the darkening towards high power / low speed to orient it.

            Goal: pick {{goalText}}.

            Reply with JSON exactly like:
            {"row": 0, "column": 0, "confidence": 0.0, "reasoning": "one or two sentences", "alternative": {"row": 0, "column": 0}, "advice": "optional short tip"}
            row and column are zero-based (row 0 = top = slowest, column 0 = left = lowest power). Use "alternative": null if there is none.
            """;
    }

    public static GridReading Parse(string reply, MaterialTestOptions g)
    {
        var o = AiJson.ExtractObject(reply)
                ?? throw new AiException(AiErrorKind.BadResponse, "The AI couldn't read the test grid. Try a sharper, straight-on photo in good light.");
        int rows = Math.Max(1, g.SpeedSteps), cols = Math.Max(1, g.PowerSteps);
        int? row = o.Int("row"), col = o.Int("column") ?? o.Int("col");
        if (row is null || col is null)
            throw new AiException(AiErrorKind.BadResponse, "The AI couldn't pick a cell from that photo. Try a sharper, straight-on photo in good light.");
        if (row < 0 || row >= rows || col < 0 || col >= cols)
            throw new AiException(AiErrorKind.BadResponse,
                $"The AI picked a cell outside the {rows} × {cols} grid. Check the grid settings match the burn you photographed.");

        (int, int)? alt = null;
        if (o["alternative"] is System.Text.Json.Nodes.JsonObject a && a.Int("row") is { } ar && (a.Int("column") ?? a.Int("col")) is { } ac &&
            ar >= 0 && ar < rows && ac >= 0 && ac < cols && (ar != row || ac != col))
            alt = (ar, ac);

        return new GridReading
        {
            Row = row.Value,
            Column = col.Value,
            SpeedMmMin = Math.Round(Speed(g, row.Value)),
            PowerPercent = Math.Round(Power(g, col.Value), 1),
            Confidence = Math.Clamp(o.Num("confidence") ?? 0.5, 0, 1),
            Reasoning = (o.Str("reasoning") ?? "").Trim(),
            Alternative = alt,
            Advice = (o.Str("advice") ?? "").Trim(),
        };
    }

    /// <summary>Speed of a grid row, matching <see cref="MaterialTestGenerator"/>.</summary>
    public static double Speed(MaterialTestOptions g, int row) => MaterialTestGenerator.Step(g.MinSpeed, g.MaxSpeed, row, g.SpeedSteps);

    /// <summary>Power of a grid column, matching <see cref="MaterialTestGenerator"/>.</summary>
    public static double Power(MaterialTestOptions g, int column) => MaterialTestGenerator.Step(g.MinPower, g.MaxPower, column, g.PowerSteps);

    /// <summary>Turns a reading into a material preset ready to save in the library.</summary>
    public static MaterialPreset ToPreset(GridReading reading, MaterialTestOptions g, GridGoal goal, string material, double thicknessMm)
    {
        var mode = goal == GridGoal.Cut ? LayerMode.Line : goal == GridGoal.Photo ? LayerMode.Image : g.CellMode == LayerMode.Line ? LayerMode.Line : LayerMode.Fill;
        return new MaterialPreset
        {
            Material = string.IsNullOrWhiteSpace(material) ? "My material" : material.Trim(),
            ThicknessMm = thicknessMm,
            Operation = goal switch
            {
                GridGoal.Cut => "Cut",
                GridGoal.Photo => "Photo",
                GridGoal.LightEngrave => "Light engrave",
                _ => "Engrave",
            },
            Mode = mode,
            SpeedMmMin = reading.SpeedMmMin,
            PowerPercent = reading.PowerPercent,
            Passes = 1,
            LineIntervalMm = g.LineIntervalMm,
            AirAssist = goal == GridGoal.Cut,
            Notes = $"From a test grid read by AI on {DateTime.Now.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} " +
                    $"(row {reading.Row + 1}, column {reading.Column + 1}, {reading.Confidence:P0} confident). {reading.Reasoning}".Trim(),
        };
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
