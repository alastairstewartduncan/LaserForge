using System.Text;
using System.Text.Json.Nodes;
using AsdLasercraft.Core.Ai.Library;

namespace AsdLasercraft.Core.Ai;

/// <summary>What the critic agent made of a design.</summary>
public sealed class CriticVerdict
{
    public bool Accept { get; init; }
    /// <summary>0–10, or -1 when the critic didn't give one.</summary>
    public int Score { get; init; } = -1;
    public List<string> Issues { get; init; } = new();
    /// <summary>What the critic wants changed, written for the designer.</summary>
    public string Changes { get; init; } = "";
    public string Praise { get; init; } = "";
}

/// <summary>One draft in a refinement run, with what the checks and the critic said about it.</summary>
public sealed class RefineRound
{
    public int Number { get; init; }
    public DesignResult Design { get; init; } = null!;
    public List<DesignFinding> Findings { get; init; } = new();
    public CriticVerdict? Critic { get; set; }
    public int ProblemCount => Findings.Count(f => f.Severity == FindingSeverity.Problem);
    public int WarningCount => Findings.Count(f => f.Severity == FindingSeverity.Warning);
    /// <summary>Passed the hard checks and (if there was a critic) was accepted.</summary>
    public bool Passed => ProblemCount == 0 && (Critic == null || Critic.Accept);
}

public sealed class RefineOutcome
{
    public List<RefineRound> Rounds { get; } = new();
    /// <summary>The draft to offer the user: the best of the rounds, not necessarily the last.</summary>
    public RefineRound Best { get; set; } = null!;
    public RefineRound First => Rounds[0];
    /// <summary>Why the run stopped, in plain words.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Check codes that came up in more than one round: a hint that a skill should cover them.</summary>
    public List<DesignFinding> Recurring { get; } = new();
    public bool Improved => Rounds.Count > 1 && !ReferenceEquals(Best, First);
}

public sealed class RefineOptions
{
    /// <summary>How many times the designer may revise after the first draft (1–4).</summary>
    public int MaxRevisions { get; set; } = 2;
    public DesignStyle Style { get; set; } = DesignStyle.Engrave;
    public double BedWidthMm { get; set; }
    public double BedHeightMm { get; set; }
    /// <summary>The client for the critic; null runs the hard checks only.</summary>
    public IAiClient? CriticClient { get; set; }
    /// <summary>Composed critic instructions (agent + skills).</summary>
    public string? CriticInstructions { get; set; }
    public string? CriticModel { get; set; }
    public Action<string>? Progress { get; set; }
}

/// <summary>
/// The refinement loop: the designer drafts, hard checks and a critic agent review, the designer revises, and so on
/// until the draft passes or the round limit is reached. Every draft is kept so the user can compare and choose.
/// </summary>
public sealed class DesignRefiner
{
    private readonly DesignGenerator _designer;
    private readonly RefineOptions _o;

    public DesignRefiner(DesignGenerator designer, RefineOptions options)
    {
        _designer = designer;
        _o = options;
    }

    /// <summary>Runs the loop from a fresh brief.</summary>
    public async Task<RefineOutcome> RunAsync(DesignRequest request, CancellationToken ct = default)
    {
        _o.Progress?.Invoke("Drawing the first draft…");
        var first = await _designer.GenerateAsync(request, ct);
        return await ContinueAsync(first, request, request.Description, ct);
    }

    /// <summary>Runs the loop starting from a design that already exists (e.g. the result of <see cref="DesignGenerator.ReviseAsync"/>).</summary>
    public async Task<RefineOutcome> ContinueAsync(DesignResult first, DesignRequest request, string brief, CancellationToken ct = default)
    {
        var outcome = new RefineOutcome();
        int maxRevisions = Math.Clamp(_o.MaxRevisions, 0, 4);
        var design = first;

        for (int n = 1; ; n++)
        {
            var round = new RefineRound { Number = n, Design = design, Findings = DesignChecks.Run(design, request, _o.Style, _o.BedWidthMm, _o.BedHeightMm) };
            outcome.Rounds.Add(round);

            if (_o.CriticClient != null)
            {
                _o.Progress?.Invoke(n == 1 ? "The critic is reviewing the draft…" : $"The critic is reviewing revision {n - 1}…");
                try { round.Critic = await ReviewAsync(design, request, brief, round.Findings, ct); }
                catch (AiException e) when (e.Kind is not (AiErrorKind.Cancelled or AiErrorKind.NoKey or AiErrorKind.BadKey))
                {
                    // A critic that fails to answer shouldn't lose the design: carry on with the hard checks alone.
                    outcome.Summary = "The critic couldn't be reached, so only the built-in checks were used. ";
                }
            }

            if (round.Passed) { outcome.Summary += n == 1 ? "The first draft passed review." : $"Passed review after {n - 1} revision(s)."; break; }
            if (n > maxRevisions) { outcome.Summary += $"Stopped after {maxRevisions} revision(s); showing the best draft."; break; }

            _o.Progress?.Invoke($"Revising (round {n} of {maxRevisions})…");
            try { design = await _designer.RefineAsync(Feedback(round), ct); }
            catch (AiException e) when (e.Kind == AiErrorKind.BadResponse)
            {
                outcome.Summary += "A revision came back unreadable, so the best earlier draft is shown.";
                break;
            }
        }

        outcome.Best = outcome.Rounds
            .OrderByDescending(r => r.Passed)
            .ThenBy(r => r.ProblemCount)
            .ThenByDescending(r => r.Critic?.Score ?? -1)
            .ThenBy(r => r.WarningCount)
            .ThenByDescending(r => r.Number)
            .First();

        foreach (var g in outcome.Rounds.SelectMany(r => r.Findings.Where(f => f.Severity != FindingSeverity.Note).Select(f => (r.Number, f)))
                                        .GroupBy(x => x.f.Code).Where(g => g.Select(x => x.Number).Distinct().Count() > 1))
            outcome.Recurring.Add(g.Last().f);
        return outcome;
    }

    /// <summary>The revision request sent to the designer: hard problems first, then the critic's changes.</summary>
    public static string Feedback(RefineRound round)
    {
        var sb = new StringBuilder("A review of this draft found the following. Fix every point and keep everything else as it is.\n");
        var checks = DesignChecks.Describe(round.Findings);
        if (checks.Length > 0) sb.Append("\nMeasured problems:\n").Append(checks).Append('\n');
        if (round.Critic is { Accept: false } c)
        {
            if (c.Issues.Count > 0) sb.Append("\nReviewer's points:\n").Append(string.Join("\n", c.Issues.Select(i => "- " + i))).Append('\n');
            if (c.Changes.Length > 0) sb.Append("\nRequested changes: ").Append(c.Changes).Append('\n');
        }
        return sb.ToString().Trim();
    }

    public const string CriticSystemPrompt = """
        You review vector artwork made for a diode laser engraver, before the user sees it. You are given the brief, the SVG
        (millimetres; closed shapes are engraved solid, open paths are single burned lines, shapes inside <g id="cut"> are cut through,
        <text> is outlined in the named font) and the results of automatic measurements.

        Judge only what matters for a good burn and for meeting the brief:
        - Does it contain what the brief asked for, with any quoted wording spelt exactly?
        - Is the layout balanced and readable at the stated size? Is anything overlapping, crowded or running off the piece?
        - Will the detail survive burning (features at least about 0.5 mm, gaps at least about 0.4 mm)?
        - Are solid areas closed shapes, and is the cut outline (if any) a single closed outline around the artwork?
        Treat every measured Problem as real. Do not ask for changes for the sake of taste: if it is good enough to burn, accept it.

        Reply with ONLY a JSON object:
        {"verdict": "accept" or "revise", "score": 0-10, "praise": "one short sentence on what works",
         "issues": ["short, specific problem", ...], "changes": "clear instructions to the designer for the next draft, or empty"}
        """;

    private async Task<CriticVerdict> ReviewAsync(DesignResult design, DesignRequest request, string brief, List<DesignFinding> findings, CancellationToken ct)
    {
        var svg = design.Svg.Length > 40_000 ? design.Svg[..40_000] + "\n<!-- shortened -->" : design.Svg;
        var measured = DesignChecks.Describe(findings);
        var user = $"""
            Brief: {(string.IsNullOrWhiteSpace(brief) ? "(a change to an existing design)" : brief.Trim())}
            Size: {design.Bounds.Width:0.#} × {design.Bounds.Height:0.#} mm. Style: {_o.Style}.

            Automatic measurements:
            {(measured.Length > 0 ? measured : "- No problems measured.")}

            SVG:
            ```svg
            {svg}
            ```
            """;
        var response = await _o.CriticClient!.SendAsync(new AiRequest
        {
            Purpose = "critic",
            System = CriticSystemPrompt + AgentRuntime.Section("Reviewer guidance", _o.CriticInstructions ?? ""),
            Messages = { AiMessage.User(user) },
            MaxTokens = 1200,
            Model = string.IsNullOrWhiteSpace(_o.CriticModel) ? null : _o.CriticModel,
        }, ct);
        return ParseVerdict(response.Text);
    }

    /// <summary>Reads the critic's JSON. An unreadable reply counts as acceptance, so a confused critic can't stall the loop.</summary>
    public static CriticVerdict ParseVerdict(string text)
    {
        var o = AiJson.ExtractObject(text);
        if (o == null) return new CriticVerdict { Accept = true, Praise = "" };
        var verdict = (o.Str("verdict") ?? "").Trim().ToLowerInvariant();
        var issues = (o["issues"] as JsonArray)?.Select(i => i?.ToString() ?? "").Where(s => s.Trim().Length > 0).Select(s => s.Trim()).Take(8).ToList() ?? new();
        var changes = (o.Str("changes") ?? "").Trim();
        bool accept = verdict.StartsWith("accept") || verdict is "ok" or "pass" or "approved" || (verdict.Length == 0 && issues.Count == 0 && changes.Length == 0);
        return new CriticVerdict
        {
            Accept = accept,
            Score = o.Int("score") is { } s ? Math.Clamp(s, 0, 10) : -1,
            Issues = issues,
            Changes = changes,
            Praise = (o.Str("praise") ?? "").Trim(),
        };
    }
}
