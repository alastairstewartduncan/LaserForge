using System.Globalization;
using System.Text.RegularExpressions;
using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Ai;

public enum FindingSeverity { Note, Warning, Problem }

/// <summary>Something a check found in a generated design. <see cref="Code"/> is stable so repeats can be spotted.</summary>
public sealed record DesignFinding(FindingSeverity Severity, string Code, string Message);

/// <summary>
/// Hard checks on a generated design, done in code rather than left to a model's opinion: does it fit, will the
/// solid areas actually fill, is anything too small to burn, is the cut outline there, is the wording present.
/// </summary>
public static class DesignChecks
{
    public const double MinFeatureMm = 0.4;
    public const double GapToleranceMm = 0.5;

    public static List<DesignFinding> Run(DesignResult d, DesignRequest request, DesignStyle style, double bedWidthMm = 0, double bedHeightMm = 0)
    {
        var f = new List<DesignFinding>();
        string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var b = d.Bounds;

        // ---- size
        if (bedWidthMm > 0 && bedHeightMm > 0 && (b.Width > bedWidthMm + 0.01 || b.Height > bedHeightMm + 0.01))
            f.Add(new(FindingSeverity.Problem, "too-big-for-bed", $"The design is {Mm(b.Width)} × {Mm(b.Height)} mm, larger than the {Mm(bedWidthMm)} × {Mm(bedHeightMm)} mm bed."));
        if (request.HeightMm > 0 && request.WidthMm > 0)
        {
            double wanted = request.WidthMm / request.HeightMm, got = b.Height > 1e-6 ? b.Width / b.Height : 0;
            if (got > 0 && (got / wanted > 1.35 || wanted / got > 1.35))
                f.Add(new(FindingSeverity.Warning, "wrong-proportions", $"The design came out {Mm(b.Width)} × {Mm(b.Height)} mm, a different shape from the {Mm(request.WidthMm)} × {Mm(request.HeightMm)} mm asked for. Compose it to fill that shape."));
        }

        // ---- solid areas
        var solid = d.Closed.Concat(d.Text).ToList();
        if (style != DesignStyle.LineArt && solid.Count == 0)
            f.Add(new(FindingSeverity.Problem, "nothing-to-fill", "There are no closed shapes, so nothing would be engraved solid. Draw solid areas as closed outlines."));

        if (style != DesignStyle.LineArt)
        {
            int nearlyClosed = d.Open.Count(p => p.Points.Count >= 4 && p.Length > 4 && Vec2.Distance(p.Points[0], p.Points[^1]) < GapToleranceMm);
            if (nearlyClosed > 0)
                f.Add(new(FindingSeverity.Problem, "open-shapes", $"{nearlyClosed} shape(s) are almost closed but have a small gap, so they would burn as outlines instead of being filled. Close them (end each path with Z)."));
        }

        // ---- too small to burn
        int tiny = 0, slivers = 0;
        foreach (var p in d.Closed)
        {
            var pb = p.Bounds;
            if (Math.Max(pb.Width, pb.Height) < MinFeatureMm) { tiny++; continue; }
            double perimeter = p.Length;
            // Mean half-width of a shape is roughly area ÷ half its perimeter: very small means a sliver.
            if (perimeter > 2 && Math.Abs(p.SignedArea) * 2 / perimeter < MinFeatureMm / 2.5) slivers++;
        }
        if (tiny > 0)
            f.Add(new(FindingSeverity.Warning, "tiny-shapes", $"{tiny} shape(s) are smaller than {Mm(MinFeatureMm)} mm and would burn as specks. Remove them or make them bigger."));
        if (slivers > 0)
            f.Add(new(FindingSeverity.Warning, "slivers", $"{slivers} filled shape(s) are hair-thin (under about {Mm(MinFeatureMm)} mm wide). Thicken them to at least 0.5 mm or draw them as single open lines."));

        // ---- the same path twice burns twice
        int dupes = CountDuplicates(d.Closed.Concat(d.Open).Concat(d.Cut).ToList());
        if (dupes > 0)
            f.Add(new(FindingSeverity.Warning, "duplicate-paths", $"{dupes} path(s) are drawn twice in the same place and would be burned twice. Remove the copies."));

        // ---- cut outline
        if (style == DesignStyle.EngraveAndCut)
        {
            if (d.Cut.Count == 0)
                f.Add(new(FindingSeverity.Problem, "no-cut-outline", "There is no cut outline. Put one closed outline around the whole piece inside <g id=\"cut\">."));
            else
            {
                if (d.Cut.Any(c => !c.Closed))
                    f.Add(new(FindingSeverity.Problem, "cut-not-closed", "The cut outline isn't a closed shape, so the piece wouldn't come free. Close it."));
                var cutBounds = Bounds.Empty;
                foreach (var c in d.Cut) cutBounds = cutBounds.Union(c.Bounds);
                var art = Bounds.Empty;
                foreach (var p in solid.Concat(d.Open)) art = art.Union(p.Bounds);
                if (!art.IsEmpty && (art.MinX < cutBounds.MinX - 0.05 || art.MinY < cutBounds.MinY - 0.05 || art.MaxX > cutBounds.MaxX + 0.05 || art.MaxY > cutBounds.MaxY + 0.05))
                    f.Add(new(FindingSeverity.Problem, "art-outside-cut", "Some of the engraving sits outside the cut outline and would be cut off. Keep all artwork at least 2 mm inside the outline."));
            }
        }
        else if (d.Cut.Count > 0 && style == DesignStyle.Engrave)
        {
            f.Add(new(FindingSeverity.Note, "unexpected-cut", "The design includes a cut outline although an engrave-only design was asked for."));
        }

        // ---- wording the user put in quotes must be there
        foreach (var phrase in QuotedPhrases(request.Description))
        {
            if (!ContainsWording(d.Svg, phrase))
                f.Add(new(FindingSeverity.Problem, "missing-wording", $"The wording \"{phrase}\" from the brief isn't in the design (or is spelt differently). Include it exactly, as <text>."));
        }

        // ---- sheer size
        int points = d.All.Sum(p => p.Points.Count);
        if (points > 60_000)
            f.Add(new(FindingSeverity.Warning, "too-detailed", "The design is extremely detailed and will be slow to burn. Simplify it."));

        return f;
    }

    private static int CountDuplicates(List<Polyline> paths)
    {
        var seen = new HashSet<string>();
        int dupes = 0;
        foreach (var p in paths)
        {
            if (p.Points.Count < 2) continue;
            var b = p.Bounds;
            var key = string.Create(CultureInfo.InvariantCulture, $"{p.Points.Count}|{p.Closed}|{b.MinX:0.00}|{b.MinY:0.00}|{b.MaxX:0.00}|{b.MaxY:0.00}|{p.Length:0.0}");
            if (!seen.Add(key)) dupes++;
        }
        return dupes;
    }

    private static readonly Regex Quoted = new("[\"“]([^\"”]{1,80})[\"”]", RegexOptions.Compiled);
    private static readonly Regex TextElement = new(@"<text\b[^>]*>(.*?)</text>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IEnumerable<string> QuotedPhrases(string description) =>
        Quoted.Matches(description ?? "").Select(m => m.Groups[1].Value.Trim()).Where(s => s.Any(char.IsLetterOrDigit)).Distinct();

    /// <summary>
    /// True when the SVG's lettering contains the phrase, ignoring case, spacing and punctuation. Curved text is one
    /// &lt;text&gt; per character, so all text elements are joined in document order before comparing.
    /// </summary>
    public static bool ContainsWording(string svg, string phrase)
    {
        static string Letters(string s) => new(System.Net.WebUtility.HtmlDecode(s).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var all = string.Concat(TextElement.Matches(svg ?? "").Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")));
        var want = Letters(phrase);
        if (want.Length == 0) return true;
        var have = Letters(all);
        if (have.Contains(want)) return true;
        // Text along the bottom of a circle is sometimes laid out right-to-left.
        var reversed = new string(want.Reverse().ToArray());
        return have.Contains(reversed);
    }

    /// <summary>The findings as a short list a model (or a person) can act on.</summary>
    public static string Describe(IEnumerable<DesignFinding> findings) =>
        string.Join("\n", findings.Where(x => x.Severity != FindingSeverity.Note).Select(x => $"- [{x.Severity}] {x.Message}"));
}
