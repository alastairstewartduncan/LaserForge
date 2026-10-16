using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Import;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;
using static AsdLasercraft.Core.Ai.AiJson;

namespace AsdLasercraft.Core.Ai;

/// <summary>
/// What the assistant can see and change. The desktop app implements this over its live project so each tool call
/// becomes one normal, undoable edit. There is deliberately no way to start, frame or stream a job from here.
/// </summary>
public interface IAssistantHost
{
    LaserProject Project { get; }
    IReadOnlyCollection<Guid> Selection { get; }
    MaterialLibrary Materials { get; }
    ITextOutliner Outliner { get; }
    IReadOnlyList<string> InstalledFonts { get; }
    /// <summary>The last few lines from the laser console (for "what does this alarm mean?").</summary>
    IReadOnlyList<string> RecentConsole { get; }
    /// <summary>Called before a tool changes the project (take an undo snapshot).</summary>
    void BeginEdit(string description);
    /// <summary>Called after a change; <paramref name="select"/> is what to select afterwards (null = keep).</summary>
    void EndEdit(IEnumerable<Guid>? select);
    /// <summary>Asks the design generator for vectors; null when AI design isn't available.</summary>
    Task<DesignResult>? GenerateDesign(DesignRequest request, CancellationToken ct);
}

/// <summary>A change the assistant made, shown as a chip under its reply.</summary>
public sealed record AssistantAction(string Tool, string Summary, bool Failed = false);

/// <summary>The assistant's tool definitions and their implementations.</summary>
public sealed class AssistantTools
{
    private readonly IAssistantHost _host;
    /// <summary>Shapes added by the assistant's most recent tool call ("last_added" target).</summary>
    private List<Guid> _lastAdded = new();

    public AssistantTools(IAssistantHost host) { _host = host; }

    public static readonly string[] ReadOnlyTools = { "get_project", "check_job" };

    public static List<AiTool> Definitions(bool canEdit)
    {
        // JSON nodes can only have one parent, so shared property schemas are built fresh each time.
        static JsonObject target() => StringProp("Which shapes: \"selection\", \"all\", \"last_added\" (shapes you just made), \"layer:<name>\" or \"name:<text>\" (name contains).");
        static JsonObject layer() => StringProp("Layer name (or 1-based position). Omit to use a sensible default for the shape.");
        var tools = new List<AiTool>
        {
            new("get_project", "Returns the current project: bed size, machine, layers with all settings, and every shape (name, layer, position, size). Call this after making changes if you need fresh numbers.",
                Schema()),
            new("check_job", "Runs the app's pre-flight checks and time estimate on the current project (like pressing Start, but nothing is sent to the laser).",
                Schema()),
        };
        if (!canEdit) return tools;

        tools.AddRange(new[]
        {
            new AiTool("set_layer_settings", "Changes a layer's burn settings. Only the fields you pass are changed.",
                Schema(("layer", StringProp("Layer name or 1-based position."), true),
                       ("name", StringProp("New layer name."), false),
                       ("mode", StringProp("Line = trace outlines (cut/score), Fill = engrave inside closed shapes, Image = raster bitmaps.", "Line", "Fill", "Image"), false),
                       ("speed_mm_min", NumberProp("Feed rate while burning, mm/min."), false),
                       ("power_percent", NumberProp("Laser power 0–100 %."), false),
                       ("passes", IntProp("Number of passes, 1–20."), false),
                       ("line_interval_mm", NumberProp("Fill/image scan spacing in mm (0.05–0.3 typical)."), false),
                       ("air_assist", BoolProp("Air assist on (M8)."), false),
                       ("fill_angle_deg", NumberProp("Fill scan direction in degrees."), false),
                       ("cross_hatch", BoolProp("Second fill pass at 90°."), false),
                       ("kerf_mm", NumberProp("Line layers: kerf compensation width in mm."), false),
                       ("tabs", IntProp("Line layers: uncut bridges per closed outline."), false),
                       ("output", BoolProp("Whether the layer burns."), false))),
            new AiTool("apply_material_preset", "Copies a saved material preset (speed, power, passes, mode…) onto a layer. Use get_project to see preset names.",
                Schema(("layer", StringProp("Layer name or 1-based position."), true),
                       ("preset", StringProp("Preset name, e.g. \"Plywood (birch) 3 mm – Cut\" (closest match is used)."), true))),
            new AiTool("add_layer", "Adds a new layer.",
                Schema(("name", StringProp("Layer name."), true),
                       ("mode", StringProp("Layer mode.", "Line", "Fill", "Image"), true),
                       ("speed_mm_min", NumberProp("Speed, mm/min."), false),
                       ("power_percent", NumberProp("Power %."), false),
                       ("passes", IntProp("Passes."), false))),
            new AiTool("add_text", "Adds text outlined in an installed font. Position is the top-left of the text in mm (bed origin top-left, Y down). Use centre_on_bed to centre it.",
                Schema(("text", StringProp("The exact text."), true),
                       ("size_mm", NumberProp("Font size in mm (capital letters are about 70 % of this)."), true),
                       ("font", StringProp("Installed font family, e.g. Arial, Georgia."), false),
                       ("bold", BoolProp("Bold."), false),
                       ("italic", BoolProp("Italic."), false),
                       ("x_mm", NumberProp("Left edge, mm."), false),
                       ("y_mm", NumberProp("Top edge, mm."), false),
                       ("centre_on_bed", BoolProp("Centre it on the bed (ignores x/y)."), false),
                       ("layer", layer(), false))),
            new AiTool("add_shape", "Adds a basic shape. Position is the top-left of its bounding box in mm.",
                Schema(("shape", StringProp("Shape type.", "rectangle", "rounded_rectangle", "ellipse", "circle", "polygon", "star"), true),
                       ("x_mm", NumberProp("Left, mm."), true),
                       ("y_mm", NumberProp("Top, mm."), true),
                       ("width_mm", NumberProp("Width, mm."), true),
                       ("height_mm", NumberProp("Height, mm (circle: ignored)."), false),
                       ("corner_radius_mm", NumberProp("rounded_rectangle corner radius."), false),
                       ("sides", IntProp("polygon/star points, 3–40."), false),
                       ("layer", layer(), false))),
            new AiTool("move_shapes", "Moves shapes so their combined top-left is at (x_mm, y_mm), or centres them on the bed.",
                Schema(("target", target(), true),
                       ("x_mm", NumberProp("New left, mm."), false),
                       ("y_mm", NumberProp("New top, mm."), false),
                       ("centre_on_bed", BoolProp("Centre on the bed instead."), false))),
            new AiTool("resize_shapes", "Scales shapes about their combined top-left corner.",
                Schema(("target", target(), true),
                       ("width_mm", NumberProp("New width, mm."), false),
                       ("height_mm", NumberProp("New height, mm."), false),
                       ("keep_aspect", BoolProp("Keep proportions (default true)."), false))),
            new AiTool("array_shapes", "Copies shapes into a grid (columns × rows, spaced by their size plus the gap) or round a circle.",
                Schema(("target", target(), true),
                       ("kind", StringProp("Grid or circular.", "grid", "circular"), true),
                       ("columns", IntProp("Grid columns."), false),
                       ("rows", IntProp("Grid rows."), false),
                       ("gap_x_mm", NumberProp("Grid horizontal gap."), false),
                       ("gap_y_mm", NumberProp("Grid vertical gap."), false),
                       ("count", IntProp("Circular: total items."), false),
                       ("radius_mm", NumberProp("Circular: radius from the circle centre to the original."), false))),
            new AiTool("move_to_layer", "Puts shapes on another layer.",
                Schema(("target", target(), true), ("layer", StringProp("Layer name or 1-based position."), true))),
            new AiTool("select", "Selects shapes in the editor so the user can see them.",
                Schema(("target", StringProp("Same targets as other tools, plus \"none\"."), true))),
            new AiTool("delete_shapes", "Deletes shapes. Only use when the user asks to remove something (it can be undone).",
                Schema(("target", target(), true))),
            new AiTool("generate_design", "Creates new vector artwork from a description (logo, badge, silhouette, pattern, lettering layout) and adds it to the project.",
                Schema(("description", StringProp("What to draw, including any exact wording."), true),
                       ("width_mm", NumberProp("Width in mm."), true),
                       ("height_mm", NumberProp("Height in mm, optional."), false),
                       ("style", StringProp("engrave = solid filled, line_art = outlines for scoring/cutting, engrave_and_cut = engraving with a cut outline.", "engrave", "line_art", "engrave_and_cut"), false),
                       ("x_mm", NumberProp("Left, mm (default: centred on the bed)."), false),
                       ("y_mm", NumberProp("Top, mm."), false))),
        });
        return tools;
    }

    /// <summary>Runs one tool call. Never throws: failures come back as an error result the model can read.</summary>
    public async Task<(AiToolResult Result, AssistantAction? Action)> ExecuteAsync(AiToolUse call, bool canEdit, CancellationToken ct)
    {
        try
        {
            if (!canEdit && !ReadOnlyTools.Contains(call.Name))
                return (new AiToolResult(call.Id, "Editing is switched off in the assistant settings. Tell the user how to do it instead.", true), null);

            var (text, summary) = call.Name switch
            {
                "get_project" => (DescribeProject(), (string?)null),
                "check_job" => (CheckJob(), "Checked the job"),
                "set_layer_settings" => SetLayer(call.Input),
                "apply_material_preset" => ApplyPreset(call.Input),
                "add_layer" => AddLayer(call.Input),
                "add_text" => AddText(call.Input),
                "add_shape" => AddShape(call.Input),
                "move_shapes" => MoveShapes(call.Input),
                "resize_shapes" => ResizeShapes(call.Input),
                "array_shapes" => ArrayShapes(call.Input),
                "move_to_layer" => MoveToLayer(call.Input),
                "select" => SelectShapes(call.Input),
                "delete_shapes" => DeleteShapes(call.Input),
                "generate_design" => await GenerateDesign(call.Input, ct),
                _ => throw new ToolError($"Unknown tool '{call.Name}'."),
            };
            return (new AiToolResult(call.Id, text), summary == null ? null : new AssistantAction(call.Name, summary));
        }
        catch (ToolError e)
        {
            return (new AiToolResult(call.Id, e.Message, true), new AssistantAction(call.Name, e.Message, Failed: true));
        }
        catch (AiException e)
        {
            return (new AiToolResult(call.Id, e.Message, true), new AssistantAction(call.Name, e.Message, Failed: true));
        }
    }

    private sealed class ToolError : Exception { public ToolError(string m) : base(m) { } }

    private LaserProject P => _host.Project;

    // ================================================================== read

    public string DescribeProject()
    {
        var sb = new StringBuilder();
        var m = P.Machine;
        sb.AppendLine(Inv($"Machine: {m.Name}; bed {m.BedWidthMm:0.#} × {m.BedHeightMm:0.#} mm; max S {m.MaxSpindleS:0}; dynamic power {(m.DynamicPower ? "on (M4)" : "off (M3)")}; scan offsets: {(m.ScanOffsets.Count == 0 ? "none" : m.ScanOffsetsText())}."));
        sb.AppendLine(Inv($"Job start: {JobSettings.Describe(P.Job.Origin)}, anchor {JobSettings.Describe(P.Job.Anchor)}."));
        sb.AppendLine("Layers (burned in this order):");
        for (int i = 0; i < P.Layers.Count; i++)
        {
            var l = P.Layers[i];
            int count = P.ShapesOn(l).Count();
            sb.Append(Inv($"  {i + 1}. \"{l.Name}\" {l.Mode}, {l.SpeedMmMin:0} mm/min, {l.PowerPercent:0.#}%, {l.Passes} pass(es)"));
            if (l.Mode != LayerMode.Line) sb.Append(Inv($", interval {l.LineIntervalMm:0.###} mm"));
            if (l.Mode == LayerMode.Fill && (l.FillAngleDeg != 0 || l.CrossHatch)) sb.Append(Inv($", angle {l.FillAngleDeg:0}°{(l.CrossHatch ? " cross-hatch" : "")}"));
            if (l.Mode == LayerMode.Line && (l.KerfMm > 0 || l.TabCount > 0)) sb.Append(Inv($", kerf {l.KerfMm:0.##} mm, {l.TabCount} tabs"));
            if (l.AirAssist) sb.Append(", air");
            if (!l.Output) sb.Append(", OUTPUT OFF");
            if (l.MaterialName != null) sb.Append($", material \"{l.MaterialName}\"");
            sb.AppendLine($"; {count} shape(s)");
        }
        var sel = _host.Selection.ToHashSet();
        sb.AppendLine(P.Shapes.Count == 0 ? "Shapes: none yet." : "Shapes:");
        foreach (var s in P.Shapes.Take(60))
        {
            var b = s.GetBounds();
            var layerName = P.FindLayer(s.LayerId)?.Name ?? "?";
            string kind = s is ImageShape ? "image" : "vector";
            sb.AppendLine(Inv($"  - \"{(string.IsNullOrEmpty(s.Name) ? kind : s.Name)}\" ({kind}) on \"{layerName}\" at x {b.MinX:0.#}, y {b.MinY:0.#}, {b.Width:0.#} × {b.Height:0.#} mm{(sel.Contains(s.Id) ? " [selected]" : "")}"));
        }
        if (P.Shapes.Count > 60) sb.AppendLine($"  … and {P.Shapes.Count - 60} more.");
        var ob = P.OutputBounds();
        if (!ob.IsEmpty) sb.AppendLine(Inv($"Everything that burns: x {ob.MinX:0.#}–{ob.MaxX:0.#}, y {ob.MinY:0.#}–{ob.MaxY:0.#} mm."));
        sb.AppendLine("Material presets: " + string.Join("; ", _host.Materials.Presets.Select(p => $"\"{p.DisplayName}\" ({p.Summary})")));
        return sb.ToString();
    }

    private string CheckJob()
    {
        var job = GcodeGenerator.Generate(P);
        var issues = Preflight.Check(P, job);
        var sb = new StringBuilder();
        if (!job.Bounds.IsEmpty)
            sb.AppendLine(Inv($"Job {job.Bounds.Width:0.#} × {job.Bounds.Height:0.#} mm, estimated {job.EstimatedTime:h\\:mm\\:ss}, {job.Lines.Count} G-code lines."));
        if (issues.Count == 0) sb.AppendLine("Pre-flight: no problems found.");
        foreach (var i in issues) sb.AppendLine($"{i.Severity}: {i.Message}");
        return sb.ToString();
    }

    // ================================================================== layers

    private Layer FindLayer(string? key, bool required = true)
    {
        key = (key ?? "").Trim();
        if (int.TryParse(key, out int n) && n >= 1 && n <= P.Layers.Count) return P.Layers[n - 1];
        var l = P.Layers.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? P.Layers.FirstOrDefault(x => x.Name.Contains(key, StringComparison.OrdinalIgnoreCase) && key.Length > 0)
                ?? (Enum.TryParse<LayerMode>(key, true, out var mode) ? P.Layers.FirstOrDefault(x => x.Mode == mode) : null);
        if (l == null && required)
            throw new ToolError($"No layer called '{key}'. Layers are: {string.Join(", ", P.Layers.Select(x => $"\"{x.Name}\""))}.");
        return l!;
    }

    private (string, string?) SetLayer(JsonObject a)
    {
        var l = FindLayer(a.Str("layer"));
        var changes = new List<string>();
        _host.BeginEdit($"Assistant: change layer '{l.Name}'");
        if (a.Str("name") is { Length: > 0 } name) { l.Name = name; changes.Add($"name \"{name}\""); }
        if (a.Str("mode") is { } ms && Enum.TryParse<LayerMode>(ms, true, out var mode)) { l.Mode = mode; changes.Add(mode.ToString()); }
        if (a.Num("speed_mm_min") is { } sp) { l.SpeedMmMin = Math.Clamp(sp, 1, 100_000); changes.Add(Inv($"{l.SpeedMmMin:0} mm/min")); }
        if (a.Num("power_percent") is { } pw) { l.PowerPercent = Math.Clamp(pw, 0, 100); changes.Add(Inv($"{l.PowerPercent:0.#}%")); }
        if (a.Int("passes") is { } ps) { l.Passes = Math.Clamp(ps, 1, 20); changes.Add($"{l.Passes} pass(es)"); }
        if (a.Num("line_interval_mm") is { } iv) { l.LineIntervalMm = Math.Clamp(iv, 0.02, 5); changes.Add(Inv($"interval {l.LineIntervalMm:0.###} mm")); }
        if (a.Bool("air_assist") is { } air) { l.AirAssist = air; changes.Add(air ? "air on" : "air off"); }
        if (a.Num("fill_angle_deg") is { } fa) { l.FillAngleDeg = Math.Clamp(fa, -360, 360); changes.Add(Inv($"angle {fa:0}°")); }
        if (a.Bool("cross_hatch") is { } ch) { l.CrossHatch = ch; changes.Add(ch ? "cross-hatch" : "no cross-hatch"); }
        if (a.Num("kerf_mm") is { } k) { l.KerfMm = Math.Clamp(k, 0, 2); changes.Add(Inv($"kerf {l.KerfMm:0.##} mm")); }
        if (a.Int("tabs") is { } t) { l.TabCount = Math.Clamp(t, 0, 50); changes.Add($"{l.TabCount} tabs"); }
        if (a.Bool("output") is { } o) { l.Output = o; changes.Add(o ? "output on" : "output off"); }
        _host.EndEdit(null);
        if (changes.Count == 0) return ("Nothing to change – pass at least one setting.", null);
        var summary = $"Layer '{l.Name}': {string.Join(", ", changes)}";
        return (summary, summary);
    }

    private (string, string?) ApplyPreset(JsonObject a)
    {
        var l = FindLayer(a.Str("layer"));
        var wanted = (a.Str("preset") ?? "").Trim();
        var preset = BestPreset(wanted) ?? throw new ToolError($"No preset like '{wanted}'. Presets: {string.Join("; ", _host.Materials.Presets.Select(p => p.DisplayName))}.");
        _host.BeginEdit($"Assistant: apply {preset.DisplayName}");
        preset.ApplyTo(l);
        _host.EndEdit(null);
        var summary = $"Applied \"{preset.DisplayName}\" to '{l.Name}' ({preset.Summary})";
        return (summary, summary);
    }

    private MaterialPreset? BestPreset(string wanted)
    {
        if (wanted.Length == 0) return null;
        var presets = _host.Materials.Presets;
        var exact = presets.FirstOrDefault(p => p.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // Score by shared words, so "birch ply cut" finds "Plywood (birch) 3 mm – Cut".
        var words = Words(wanted);
        return presets.Select(p => (p, score: Words(p.DisplayName).Count(w => words.Any(x => w.StartsWith(x) || x.StartsWith(w)))))
                      .Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.p).FirstOrDefault();

        static List<string> Words(string s) => s.ToLowerInvariant()
            .Split(new[] { ' ', '(', ')', '–', '-', ',', '/' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
    }

    private (string, string?) AddLayer(JsonObject a)
    {
        var mode = Enum.TryParse<LayerMode>(a.Str("mode"), true, out var m) ? m : LayerMode.Line;
        var layer = LaserProject.DefaultLayer(mode);
        layer.Name = a.Str("name") is { Length: > 0 } n ? n : $"Layer {P.Layers.Count + 1}";
        if (a.Num("speed_mm_min") is { } sp) layer.SpeedMmMin = Math.Clamp(sp, 1, 100_000);
        if (a.Num("power_percent") is { } pw) layer.PowerPercent = Math.Clamp(pw, 0, 100);
        if (a.Int("passes") is { } ps) layer.Passes = Math.Clamp(ps, 1, 20);
        _host.BeginEdit($"Assistant: add layer '{layer.Name}'");
        P.Layers.Add(layer);
        _host.EndEdit(null);
        var summary = $"Added layer '{layer.Name}' ({layer})";
        return (summary, summary);
    }

    // ================================================================== shapes

    private Layer DefaultVectorLayer(string? key, LayerMode preferred)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var l = FindLayer(key);
            if (l.Mode == LayerMode.Image) throw new ToolError($"Layer '{l.Name}' is an Image layer; vector shapes need a Line or Fill layer.");
            return l;
        }
        return P.Layers.FirstOrDefault(l => l.Mode == preferred) ?? P.GetOrCreateLayer(preferred);
    }

    private (string, string?) AddText(JsonObject a)
    {
        var text = a.Str("text");
        if (string.IsNullOrWhiteSpace(text)) throw new ToolError("text is required.");
        double size = Math.Clamp(a.Num("size_mm") ?? 10, 0.5, 500);
        var font = ResolveFont(a.Str("font"));
        var paths = _host.Outliner.Outline(text, font, size, a.Bool("bold") ?? false, a.Bool("italic") ?? false, TextAnchor.Start);
        if (paths.Count == 0) throw new ToolError($"The font '{font}' produced no outlines for that text.");
        var shape = new VectorShape(paths, $"Text \"{text}\"");
        var layer = DefaultVectorLayer(a.Str("layer"), LayerMode.Fill);
        shape.LayerId = layer.Id;
        Place(shape, a);
        AddShapes($"Assistant: add text \"{text}\"", shape);
        var b = shape.GetBounds();
        return (Inv($"Added text \"{text}\" in {font} on '{layer.Name}' at x {b.MinX:0.#}, y {b.MinY:0.#}, {b.Width:0.#} × {b.Height:0.#} mm."),
                $"Added text \"{text}\" ({font}, {size:0.#} mm)");
    }

    private string ResolveFont(string? wanted)
    {
        var fonts = _host.InstalledFonts;
        if (fonts.Count == 0) return string.IsNullOrWhiteSpace(wanted) ? "Arial" : wanted;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var match = fonts.FirstOrDefault(f => f.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        ?? fonts.FirstOrDefault(f => f.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return fonts.FirstOrDefault(f => f == "Arial") ?? fonts[0];
    }

    private (string, string?) AddShape(JsonObject a)
    {
        var kind = (a.Str("shape") ?? "rectangle").ToLowerInvariant();
        double x = a.Num("x_mm") ?? 10, y = a.Num("y_mm") ?? 10;
        double w = Math.Clamp(a.Num("width_mm") ?? 50, 0.5, 2000);
        double h = kind == "circle" ? w : Math.Clamp(a.Num("height_mm") ?? w, 0.5, 2000);
        int sides = Math.Clamp(a.Int("sides") ?? 5, 3, 40);
        VectorShape shape = kind switch
        {
            "rounded_rectangle" => ShapeFactory.Rectangle(x, y, w, h, Math.Clamp(a.Num("corner_radius_mm") ?? Math.Min(w, h) * 0.1, 0, Math.Min(w, h) / 2)),
            "ellipse" or "circle" => ShapeFactory.Ellipse(x, y, w, h),
            "polygon" => ShapeFactory.Polygon(x, y, w, h, sides),
            "star" => ShapeFactory.Polygon(x, y, w, h, sides, 0.45),
            _ => ShapeFactory.Rectangle(x, y, w, h),
        };
        shape.Name = kind.Replace('_', ' ');
        var layer = DefaultVectorLayer(a.Str("layer"), LayerMode.Line);
        shape.LayerId = layer.Id;
        AddShapes($"Assistant: add {shape.Name}", shape);
        var summary = Inv($"Added {shape.Name} {w:0.#} × {h:0.#} mm on '{layer.Name}'");
        return (summary + Inv($" at x {x:0.#}, y {y:0.#}."), summary);
    }

    private void Place(Shape shape, JsonObject a)
    {
        var b = shape.GetBounds();
        if (a.Bool("centre_on_bed") == true || (a.Num("x_mm") == null && a.Num("y_mm") == null))
            shape.MoveTo(P.Machine.BedWidthMm / 2 - b.Width / 2, P.Machine.BedHeightMm / 2 - b.Height / 2);
        else
            shape.MoveTo(a.Num("x_mm") ?? b.MinX, a.Num("y_mm") ?? b.MinY);
    }

    private void AddShapes(string description, params Shape[] shapes)
    {
        _host.BeginEdit(description);
        P.Shapes.AddRange(shapes);
        _lastAdded = shapes.Select(s => s.Id).ToList();
        _host.EndEdit(_lastAdded);
    }

    private List<Shape> Resolve(string? target)
    {
        var t = (target ?? "selection").Trim();
        List<Shape> result;
        if (t.Equals("all", StringComparison.OrdinalIgnoreCase)) result = P.Shapes.ToList();
        else if (t.Equals("last_added", StringComparison.OrdinalIgnoreCase)) result = P.Shapes.Where(s => _lastAdded.Contains(s.Id)).ToList();
        else if (t.StartsWith("layer:", StringComparison.OrdinalIgnoreCase)) { var l = FindLayer(t[6..]); result = P.ShapesOn(l).ToList(); }
        else if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            var n = t[5..].Trim();
            result = P.Shapes.Where(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            var sel = _host.Selection.ToHashSet();
            result = P.Shapes.Where(s => sel.Contains(s.Id)).ToList();
            if (result.Count == 0 && !t.Equals("selection", StringComparison.OrdinalIgnoreCase))
                result = P.Shapes.Where(s => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (result.Count == 0) throw new ToolError($"No shapes match '{t}'. Use get_project to see shape names, or ask the user to select something.");
        return result;
    }

    private (string, string?) MoveShapes(JsonObject a)
    {
        var shapes = Resolve(a.Str("target"));
        var b = ArrayTool.BoundsOf(shapes);
        Transform2D t;
        if (a.Bool("centre_on_bed") == true)
            t = Transform2D.Translate(P.Machine.BedWidthMm / 2 - b.Center.X, P.Machine.BedHeightMm / 2 - b.Center.Y);
        else
        {
            if (a.Num("x_mm") == null && a.Num("y_mm") == null) throw new ToolError("Give x_mm/y_mm or centre_on_bed.");
            t = Transform2D.Translate((a.Num("x_mm") ?? b.MinX) - b.MinX, (a.Num("y_mm") ?? b.MinY) - b.MinY);
        }
        _host.BeginEdit("Assistant: move");
        shapes.ForEach(s => s.ApplyTransform(t));
        _host.EndEdit(shapes.Select(s => s.Id));
        var nb = ArrayTool.BoundsOf(shapes);
        var summary = Inv($"Moved {shapes.Count} shape(s) to x {nb.MinX:0.#}, y {nb.MinY:0.#}");
        return (summary, summary);
    }

    private (string, string?) ResizeShapes(JsonObject a)
    {
        var shapes = Resolve(a.Str("target"));
        var b = ArrayTool.BoundsOf(shapes);
        double? w = a.Num("width_mm"), h = a.Num("height_mm");
        if (w == null && h == null) throw new ToolError("Give width_mm and/or height_mm.");
        bool keep = a.Bool("keep_aspect") ?? true;
        double sx = w is { } ww && b.Width > 1e-9 ? ww / b.Width : 1;
        double sy = h is { } hh && b.Height > 1e-9 ? hh / b.Height : 1;
        if (keep) { double s = w != null ? sx : sy; sx = sy = s; }
        if (sx <= 0 || sy <= 0 || sx > 100 || sy > 100) throw new ToolError("That size is out of range.");
        var t = Transform2D.Scale(sx, sy, new Vec2(b.MinX, b.MinY));
        _host.BeginEdit("Assistant: resize");
        shapes.ForEach(s => s.ApplyTransform(t));
        _host.EndEdit(shapes.Select(s => s.Id));
        var nb = ArrayTool.BoundsOf(shapes);
        var summary = Inv($"Resized {shapes.Count} shape(s) to {nb.Width:0.#} × {nb.Height:0.#} mm");
        return (summary, summary);
    }

    private (string, string?) ArrayShapes(JsonObject a)
    {
        var shapes = Resolve(a.Str("target"));
        List<Shape> copies;
        if ((a.Str("kind") ?? "grid").Equals("circular", StringComparison.OrdinalIgnoreCase))
        {
            int count = Math.Clamp(a.Int("count") ?? 8, 2, 360);
            double radius = Math.Clamp(a.Num("radius_mm") ?? 40, 1, 2000);
            var c = ArrayTool.BoundsOf(shapes).Center;
            copies = ArrayTool.Circular(shapes, count, new Vec2(c.X, c.Y + radius), rotateCopies: true);
        }
        else
        {
            int cols = Math.Clamp(a.Int("columns") ?? 2, 1, 100), rows = Math.Clamp(a.Int("rows") ?? 1, 1, 100);
            if (cols * rows > 2000) throw new ToolError("That's more than 2000 copies.");
            copies = ArrayTool.Grid(shapes, cols, rows, a.Num("gap_x_mm") ?? 5, a.Num("gap_y_mm") ?? 5);
        }
        if (copies.Count == 0) return ("Nothing to copy.", null);
        _host.BeginEdit("Assistant: array");
        P.Shapes.AddRange(copies);
        _lastAdded = copies.Select(s => s.Id).ToList();
        _host.EndEdit(shapes.Concat(copies).Select(s => s.Id));
        var all = ArrayTool.BoundsOf(shapes.Concat(copies));
        var summary = Inv($"Made {copies.Count} cop{(copies.Count == 1 ? "y" : "ies")} – {all.Width:0.#} × {all.Height:0.#} mm in total");
        return (summary + (P.Machine.BedWidthMm < all.MaxX || P.Machine.BedHeightMm < all.MaxY ? " (WARNING: now extends past the bed)" : ""), summary);
    }

    private (string, string?) MoveToLayer(JsonObject a)
    {
        var shapes = Resolve(a.Str("target"));
        var layer = FindLayer(a.Str("layer"));
        var movable = shapes.Where(s => s is VectorShape || layer.Mode == LayerMode.Image).ToList();
        if (movable.Count == 0) throw new ToolError($"Images can only go on Image layers, and '{layer.Name}' is a {layer.Mode} layer.");
        _host.BeginEdit($"Assistant: move to '{layer.Name}'");
        movable.ForEach(s => s.LayerId = layer.Id);
        _host.EndEdit(movable.Select(s => s.Id));
        var summary = $"Moved {movable.Count} shape(s) to '{layer.Name}'" + (movable.Count < shapes.Count ? $" ({shapes.Count - movable.Count} image(s) left where they were)" : "");
        return (summary, summary);
    }

    private (string, string?) SelectShapes(JsonObject a)
    {
        var t = a.Str("target") ?? "none";
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _host.EndEdit(Array.Empty<Guid>());
            return ("Selection cleared.", null);
        }
        var shapes = Resolve(t);
        _host.EndEdit(shapes.Select(s => s.Id));
        return ($"Selected {shapes.Count} shape(s).", null);
    }

    private (string, string?) DeleteShapes(JsonObject a)
    {
        var shapes = Resolve(a.Str("target"));
        var ids = shapes.Select(s => s.Id).ToHashSet();
        _host.BeginEdit("Assistant: delete");
        P.Shapes.RemoveAll(s => ids.Contains(s.Id));
        _host.EndEdit(Array.Empty<Guid>());
        var summary = $"Deleted {shapes.Count} shape(s)";
        return (summary + " (the user can undo with Ctrl+Z).", summary);
    }

    private async Task<(string, string?)> GenerateDesign(JsonObject a, CancellationToken ct)
    {
        var description = a.Str("description");
        if (string.IsNullOrWhiteSpace(description)) throw new ToolError("description is required.");
        var style = (a.Str("style") ?? "engrave").ToLowerInvariant() switch
        {
            "line_art" => DesignStyle.LineArt,
            "engrave_and_cut" => DesignStyle.EngraveAndCut,
            _ => DesignStyle.Engrave,
        };
        var request = new DesignRequest
        {
            Description = description,
            WidthMm = Math.Clamp(a.Num("width_mm") ?? 80, 5, 2000),
            HeightMm = Math.Clamp(a.Num("height_mm") ?? 0, 0, 2000),
            Style = style,
            AvailableFonts = _host.InstalledFonts,
        };
        var task = _host.GenerateDesign(request, ct) ?? throw new ToolError("AI design isn't available right now.");
        var result = await task;
        var shapes = DesignShapes.Build(result, P, style, a.Num("x_mm"), a.Num("y_mm"));
        _host.BeginEdit("Assistant: add design");
        P.Shapes.AddRange(shapes);
        _lastAdded = shapes.Select(s => s.Id).ToList();
        _host.EndEdit(_lastAdded);
        var b = ArrayTool.BoundsOf(shapes);
        var summary = Inv($"Added a {b.Width:0.#} × {b.Height:0.#} mm design");
        return (summary + Inv($" at x {b.MinX:0.#}, y {b.MinY:0.#} as {shapes.Count} shape(s): {string.Join(", ", shapes.Select(s => $"\"{s.Name}\" on '{P.FindLayer(s.LayerId)?.Name}'"))}. {result.Notes}"), summary);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Turns a <see cref="DesignResult"/> into shapes on sensible layers, placed on the bed.</summary>
public static class DesignShapes
{
    public static List<Shape> Build(DesignResult r, LaserProject project, DesignStyle style, double? x = null, double? y = null)
    {
        var shapes = new List<Shape>();
        Layer fill = project.Layers.FirstOrDefault(l => l.Mode == LayerMode.Fill) ?? project.GetOrCreateLayer(LayerMode.Fill);
        Layer line = project.Layers.FirstOrDefault(l => l.Mode == LayerMode.Line && !Preflight.IsCut(l))
                     ?? project.Layers.FirstOrDefault(l => l.Mode == LayerMode.Line) ?? project.GetOrCreateLayer(LayerMode.Line);
        Layer cut = project.Layers.FirstOrDefault(l => l.Mode == LayerMode.Line && Preflight.IsCut(l)) ?? line;

        void Add(IEnumerable<Polyline> paths, string name, Layer layer)
        {
            var list = paths.ToList();
            if (list.Count == 0) return;
            shapes.Add(new VectorShape(list.Select(p => p.Clone()), name) { LayerId = layer.Id });
        }

        if (style == DesignStyle.LineArt)
            Add(r.Closed.Concat(r.Open).Concat(r.Text), "AI design", line);
        else
        {
            Add(r.Closed, "AI design", fill);
            Add(r.Text, "AI lettering", fill);
            Add(r.Open, "AI design lines", line);
        }
        Add(r.Cut, "AI cut outline", cut);

        // Place the whole design as one block.
        var b = ArrayTool.BoundsOf(shapes);
        double nx = x ?? project.Machine.BedWidthMm / 2 - b.Width / 2;
        double ny = y ?? project.Machine.BedHeightMm / 2 - b.Height / 2;
        var t = Transform2D.Translate(nx - b.MinX, ny - b.MinY);
        foreach (var s in shapes) s.ApplyTransform(t);
        return shapes;
    }
}
