using System.Text.RegularExpressions;
using System.Xml;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Import;

namespace AsdLasercraft.Core.Ai;

public enum DesignStyle
{
    /// <summary>Solid shapes and lettering, filled when engraved.</summary>
    Engrave,
    /// <summary>Outlines only, for scoring or cutting (e.g. a cut-out shape or a stencil).</summary>
    LineArt,
    /// <summary>Filled artwork with a cut outline around it (coasters, tags, ornaments).</summary>
    EngraveAndCut,
}

public sealed class DesignRequest
{
    public string Description { get; set; } = "";
    public double WidthMm { get; set; } = 80;
    /// <summary>Optional; when 0 the model picks the height to suit the design.</summary>
    public double HeightMm { get; set; }
    public DesignStyle Style { get; set; } = DesignStyle.Engrave;
    /// <summary>Fonts the user has installed, so lettering uses something that will actually render.</summary>
    public IReadOnlyList<string> AvailableFonts { get; set; } = Array.Empty<string>();
}

/// <summary>A generated design, already outlined and scaled to millimetres with its top-left at (0, 0).</summary>
public sealed class DesignResult
{
    public string Svg { get; init; } = "";
    /// <summary>Closed shapes from the artwork (filled when engraved).</summary>
    public List<Polyline> Closed { get; init; } = new();
    /// <summary>Open strokes from the artwork (scored).</summary>
    public List<Polyline> Open { get; init; } = new();
    /// <summary>All lettering, outlined with the app's fonts.</summary>
    public List<Polyline> Text { get; init; } = new();
    /// <summary>For <see cref="DesignStyle.EngraveAndCut"/>: shapes the model marked as the cut outline.</summary>
    public List<Polyline> Cut { get; init; } = new();
    /// <summary>Anything the model said alongside the SVG (a one-line description of the design).</summary>
    public string Notes { get; init; } = "";
    public Bounds Bounds { get; init; }

    public IEnumerable<Polyline> All => Closed.Concat(Open).Concat(Text).Concat(Cut);
    public bool IsEmpty => !All.Any();
}

/// <summary>
/// "Describe it, get vectors": asks Claude for an SVG drawn with laser-friendly rules, then imports it,
/// outlines any lettering with real fonts and scales it to the requested size. Only the text description
/// (and, when refining, the previous SVG) leaves the PC.
/// </summary>
public sealed class DesignGenerator
{
    private readonly IAiClient _client;
    private readonly ITextOutliner _outliner;
    private readonly List<AiMessage> _history = new();
    private DesignRequest? _lastRequest;

    public DesignGenerator(IAiClient client, ITextOutliner? outliner = null)
    {
        _client = client;
        _outliner = outliner ?? PlaceholderTextOutliner.Instance;
    }

    /// <summary>True once a design has been made, so <see cref="RefineAsync"/> can adjust it.</summary>
    public bool HasDesign => _history.Count >= 2;

    public Task<DesignResult> GenerateAsync(DesignRequest request, CancellationToken ct = default)
    {
        _history.Clear();
        _lastRequest = request;
        _history.Add(AiMessage.User(BuildBrief(request)));
        return RunAsync(request, ct);
    }

    /// <summary>Changes the last design, e.g. "make the text bigger" or "add a border".</summary>
    public Task<DesignResult> RefineAsync(string change, CancellationToken ct = default)
    {
        if (!HasDesign || _lastRequest == null) throw new InvalidOperationException("Generate a design first.");
        _history.Add(AiMessage.User(
            $"Please change the design: {change.Trim()}\nKeep the same size ({SizeText(_lastRequest)}) and rules. Reply with the complete new SVG."));
        return RunAsync(_lastRequest, ct);
    }

    private async Task<DesignResult> RunAsync(DesignRequest request, CancellationToken ct)
    {
        var response = await _client.SendAsync(new AiRequest
        {
            Purpose = "design",
            System = SystemPrompt,
            Messages = _history.ToList(),
            MaxTokens = 8000,
        }, ct); // no ConfigureAwait(false): text is outlined with WPF fonts, which want the caller's (UI) thread

        var text = response.Text;
        DesignResult result;
        try
        {
            result = Parse(text, request, _outliner);
        }
        catch (AiException)
        {
            if (_history.Count > 0) _history.RemoveAt(_history.Count - 1); // let the user retry from the same point
            throw;
        }
        _history.Add(AiMessage.Assistant(text));
        return result;
    }

    // ================================================================== prompt

    public const string SystemPrompt = """
        You are the design assistant inside A·S·D Lasercraft, an app for diode laser engravers.
        You turn a short description into ONE SVG that the app imports and burns.

        Output format:
        - First one short sentence describing what you drew. Then the SVG in a ```svg fenced block. Nothing after it.
        - The <svg> must have width and height in mm (e.g. width="80mm" height="60mm") and a matching viewBox in mm (viewBox="0 0 80 60").

        Allowed elements ONLY: svg, g, path, rect, circle, ellipse, line, polyline, polygon, text (with optional tspan).
        Not allowed: image, use, symbol, defs, clipPath, mask, pattern, gradient, filter, style, class, script, foreignObject, textPath.
        Attributes that matter: d, points, x, y, width, height, rx, ry, cx, cy, r, transform, font-family, font-size, font-weight, font-style, text-anchor.
        Colour, stroke-width and opacity are ignored: the laser follows the geometry.

        How the laser reads the geometry:
        - Every CLOSED shape is filled (engraved solid). Holes are closed shapes inside closed shapes (even-odd), e.g. the counter of an "O".
        - Every OPEN path is a single burned line. Use open paths for fine line work.
        - So draw solid artwork as closed outlines of the solid areas, not as thick strokes.
        - Keep details at least 0.5 mm wide and gaps at least 0.4 mm, keep everything inside the viewBox with a margin of about 2 mm.
        - Prefer clean, bold, high-contrast silhouettes over photographic detail. No shading, hatching only if asked.

        Lettering:
        - Use <text> elements with a real font from the user's installed list, an explicit font-size (in mm) and text-anchor. The app outlines them with that font.
        - Curved text (around a circle or arc): place each character as its own <text text-anchor="middle"> rotated into place with
          transform="rotate(angle cx cy)" (or translate+rotate). Space characters by their approximate width.
        - Spell exactly what the user asked for. Never invent extra wording.

        Engrave-and-cut designs: put the cut outline(s) in <g id="cut"> as closed shapes; everything else is engraved.
        """;

    public static string BuildBrief(DesignRequest r)
    {
        var style = r.Style switch
        {
            DesignStyle.LineArt => "Line art for scoring or cutting: outlines only, mostly open strokes and simple closed outlines, no solid fills.",
            DesignStyle.EngraveAndCut => "Engraved artwork plus a cut outline around the whole piece (put the outline in <g id=\"cut\">).",
            _ => "Solid engraving: bold filled shapes and lettering.",
        };
        var fonts = r.AvailableFonts.Count > 0
            ? string.Join(", ", PreferredFonts(r.AvailableFonts))
            : "Arial, Georgia, Times New Roman, Segoe UI, Impact";
        return $"""
            Design: {r.Description.Trim()}
            Size: {SizeText(r)}
            Style: {style}
            Installed fonts you can use: {fonts}
            """;
    }

    private static string SizeText(DesignRequest r) =>
        r.HeightMm > 0 ? $"{r.WidthMm:0.#} mm wide × {r.HeightMm:0.#} mm high" : $"{r.WidthMm:0.#} mm wide (choose a height that suits the design)";

    /// <summary>A short, useful font list: well-known families first, capped so the prompt stays small.</summary>
    public static IEnumerable<string> PreferredFonts(IReadOnlyList<string> installed)
    {
        string[] favourites =
        {
            "Arial", "Arial Black", "Georgia", "Times New Roman", "Segoe UI", "Segoe Script", "Impact", "Verdana", "Trebuchet MS",
            "Palatino Linotype", "Book Antiqua", "Garamond", "Century Gothic", "Gabriola", "Lucida Handwriting", "Brush Script MT",
            "Old English Text MT", "Bahnschrift", "Courier New", "Comic Sans MS", "Cambria", "Candara", "Constantia", "Corbel",
        };
        var set = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var picked = favourites.Where(set.Contains).ToList();
        foreach (var f in installed)
        {
            if (picked.Count >= 30) break;
            if (!picked.Contains(f, StringComparer.OrdinalIgnoreCase)) picked.Add(f);
        }
        return picked.Take(30);
    }

    // ================================================================== parsing

    private static readonly Regex SvgFence = new(@"```(?:svg|xml)?\s*(<svg.*?</svg>)\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SvgBare = new(@"<svg\b.*?</svg>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ExtractSvg(string reply)
    {
        var m = SvgFence.Match(reply);
        if (m.Success) return m.Groups[1].Value;
        m = SvgBare.Match(reply);
        return m.Success ? m.Value : null;
    }

    public static DesignResult Parse(string reply, DesignRequest request, ITextOutliner outliner)
    {
        var svg = ExtractSvg(reply)
                  ?? throw new AiException(AiErrorKind.BadResponse, "The AI didn't send back a design. Try describing it a little differently.");
        var notes = SvgFence.Replace(reply, "").Replace(svg, "").Trim();
        if (notes.Length > 300) notes = notes[..300].TrimEnd() + "…";

        List<Polyline> paths;
        List<SvgText> texts;
        List<Polyline> cut;
        try
        {
            var clean = Regex.Replace(svg, @"<!DOCTYPE[^>]*>", "", RegexOptions.IgnoreCase);
            (paths, texts) = SvgImporter.ImportWithText(clean);
            cut = ExtractCutGroup(clean);
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            throw new AiException(AiErrorKind.BadResponse, "The AI's design had a drawing error and couldn't be read. Try again.", ex);
        }

        // The cut group's shapes were also imported as ordinary paths; take them out of the artwork.
        if (cut.Count > 0) paths = paths.Where(p => !cut.Any(c => SameShape(c, p))).ToList();
        var textPolys = texts.SelectMany(t => t.ToPolylines(outliner)).Where(p => p.Points.Count >= 2).ToList();

        var all = paths.Concat(textPolys).Concat(cut).ToList();
        if (all.Count == 0)
            throw new AiException(AiErrorKind.BadResponse, "The AI's design came back empty. Try again or add more detail to the description.");

        // Scale uniformly so the design fits the requested box, then put its top-left at the origin.
        var b = Bounds.Empty;
        foreach (var p in all) b = b.Union(p.Bounds);
        double sx = b.Width > 1e-6 ? request.WidthMm / b.Width : 1;
        double sy = request.HeightMm > 0 && b.Height > 1e-6 ? request.HeightMm / b.Height : sx;
        double s = Math.Min(sx, sy);
        if (b.Width <= 1e-6 && b.Height > 1e-6) s = (request.HeightMm > 0 ? request.HeightMm : request.WidthMm) / b.Height;
        var t = Transform2D.Translate(-b.MinX, -b.MinY).Then(Transform2D.Scale(s, s));

        List<Polyline> Map(IEnumerable<Polyline> src) => src.Select(p => p.Transformed(t)).ToList();
        var mappedPaths = Map(paths);
        var result = new DesignResult
        {
            Svg = svg,
            Closed = mappedPaths.Where(p => p.Closed).ToList(),
            Open = mappedPaths.Where(p => !p.Closed).ToList(),
            Text = Map(textPolys),
            Cut = Map(cut),
            Notes = notes,
            Bounds = Bounds.FromRect(0, 0, b.Width * s, b.Height * s),
        };
        return result;
    }

    /// <summary>Imports only the contents of &lt;g id="cut"&gt; (with the document's transforms).</summary>
    private static List<Polyline> ExtractCutGroup(string svg)
    {
        if (!Regex.IsMatch(svg, @"id\s*=\s*[""']cut[""']", RegexOptions.IgnoreCase)) return new();
        var doc = System.Xml.Linq.XDocument.Parse(svg);
        var group = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "g" &&
                        string.Equals((string?)e.Attribute("id"), "cut", StringComparison.OrdinalIgnoreCase));
        if (group == null) return new();
        // Hide everything outside the cut group and import what's left.
        foreach (var el in doc.Root!.Descendants().ToList())
        {
            if (el == group || el.Ancestors().Contains(group) || group.Ancestors().Contains(el)) continue;
            if (el.Name.LocalName is "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "text") el.Remove();
        }
        return SvgImporter.Import(doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
    }

    private static bool SameShape(Polyline a, Polyline b) =>
        a.Points.Count == b.Points.Count && a.Closed == b.Closed &&
        a.Points.Zip(b.Points).All(z => Math.Abs(z.First.X - z.Second.X) < 1e-6 && Math.Abs(z.First.Y - z.Second.Y) < 1e-6);
}
