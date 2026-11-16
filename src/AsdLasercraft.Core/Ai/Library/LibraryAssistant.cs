using System.Text;
using System.Text.RegularExpressions;

namespace AsdLasercraft.Core.Ai.Library;

/// <summary>A drafted or improved skill/agent for the user to review. Nothing is saved until they accept it.</summary>
public sealed class LibraryProposal
{
    public Skill? Skill { get; init; }
    public Agent? Agent { get; init; }
    /// <summary>What the AI changed or assumed, as short bullet points.</summary>
    public List<string> Notes { get; init; } = new();
    /// <summary>The proposed file as markdown, for a before/after view.</summary>
    public string Markdown => Skill?.ToMarkdown() ?? Agent?.ToMarkdown() ?? "";
    public string DisplayName => Skill?.DisplayName ?? Agent?.DisplayName ?? "";
}

/// <summary>
/// AI help for writing skills and agents: draft one from a description, turn a finished design into a skill, or
/// review and tighten an existing one. It only ever proposes. An agent's tools can never be widened by a proposal.
/// </summary>
public sealed class LibraryAssistant
{
    private readonly IAiClient _client;
    public LibraryAssistant(IAiClient client) { _client = client; }

    private const string Shared = """
        You help the user of A·S·D Lasercraft (a Windows app for GRBL diode laser engravers) write "skills" and "agents" for its AI features.

        A SKILL is a short pack of reusable guidance in markdown: a house style, a material, a product type. Good skills are specific and checkable
        ("keep engraving 2.5 mm inside the cut line", not "leave a margin"), give sizes in millimetres, explain the reason where it isn't obvious,
        and stay under about 250 words. They are instructions only: no scripts, no tools.

        An AGENT is a named role: who it is, what it cares about and how it judges. Roles: "designer" (draws vector artwork from a brief),
        "critic" (reviews a design before the user sees it), "general" (the chat assistant, which may edit the design with tools).
        Agent instructions are written to the AI in the second person ("You are…"), stay under about 200 words, and leave detailed rules to skills.

        Facts about the app that the text must respect:
        - Designs are vectors: closed shapes are engraved solid, open paths are single burned lines, a separate cut outline is cut through.
        - Only geometry is burned: no colour, shading, gradients or line thickness.
        - The AI can never start, frame or stop the laser, and nothing you write may claim or instruct otherwise.
        - Never include advice to laser PVC, vinyl or other chlorine-containing materials.

        Output format, exactly:
        1. One ```markdown fenced block containing the complete file: a header between --- lines, then the body.
        2. After the block, a line "Notes:" followed by 1 to 4 short bullet points on what you assumed or changed.
        """;

    private const string SkillHeader = """
        Skill header fields: name (lower-case-with-hyphens id), title (display name), description (one or two sentences on when to use it).
        """;

    private static string AgentHeader(IEnumerable<string> tools, IEnumerable<Skill> skills) => $"""
        Agent header fields: name (lower-case-with-hyphens id), title, description (one sentence), role (designer, critic or general),
        skills (list of skill names to attach, chosen ONLY from the existing skills below), tools (general agents only).
        Tools a general agent may be given (list only the ones the role needs; leave tools out entirely for an advice-only agent, and for designers and critics):
        {string.Join(", ", tools)}
        Existing skills: {(skills.Any() ? string.Join("; ", skills.Select(s => $"{s.Name} ({s.Description})")) : "(none yet)")}
        Do not set provider or model.
        """;

    public Task<LibraryProposal> DraftSkillAsync(string description, CancellationToken ct = default) =>
        RunAsync(SkillHeader, $"Write a new skill for this:\n{description.Trim()}", ImportAs.Skill, null, null, ct);

    public Task<LibraryProposal> DraftAgentAsync(string description, AiLibrary library, CancellationToken ct = default) =>
        RunAsync(AgentHeader(AgentRuntime.KnownToolNames(), library.Skills), $"Write a new agent for this:\n{description.Trim()}", ImportAs.Agent, library, null, ct);

    /// <summary>Writes a skill that captures the style of a finished design so later designs can match it.</summary>
    public Task<LibraryProposal> SkillFromDesignAsync(string svg, string designSummary, string userNote, CancellationToken ct = default)
    {
        if (svg.Length > 30_000) svg = svg[..30_000] + "\n<!-- shortened -->";
        return RunAsync(SkillHeader, $"""
            The user is happy with this design and wants a skill that captures its style, proportions and conventions so future designs match it.
            Describe the reusable rules (layout, proportions relative to the piece, border and margin sizes, lettering choices, level of detail),
            not this one design's subject matter.

            What the user says about it: {(string.IsNullOrWhiteSpace(userNote) ? "(nothing)" : userNote.Trim())}
            About the design: {designSummary.Trim()}

            The design as SVG in millimetres:
            ```svg
            {svg}
            ```
            """, ImportAs.Skill, null, null, ct);
    }

    public Task<LibraryProposal> ImproveAsync(Skill skill, string? feedback = null, CancellationToken ct = default) =>
        RunAsync(SkillHeader, ImproveBrief("skill", skill.ToMarkdown(), feedback), ImportAs.Skill, null, skill.Name, ct);

    public async Task<LibraryProposal> ImproveAsync(Agent agent, AiLibrary library, string? feedback = null, CancellationToken ct = default)
    {
        var p = await RunAsync(AgentHeader(agent.Tools.Where(t => t != Agent.AllTools), library.Skills), ImproveBrief("agent", agent.ToMarkdown(), feedback), ImportAs.Agent, library, agent.Name, ct);
        // An improvement keeps the user's choices: same role unless they asked, same provider/model, and never more tools than before.
        var a = p.Agent!;
        a.Provider = agent.Provider;
        a.Model = agent.Model;
        if (agent.HasAllTools) a.Tools = agent.Tools.ToList();
        else AgentRuntime.ClampTools(a, agent.Tools);
        return p;
    }

    private static string ImproveBrief(string kind, string markdown, string? feedback) => $"""
        Review and improve this {kind}. Keep its purpose and its name. Make vague instructions specific, remove repetition and anything that
        conflicts with the facts about the app, resolve contradictions, and add anything important that is missing. Don't make it longer than it needs to be.
        {(string.IsNullOrWhiteSpace(feedback) ? "" : "\nThe user (or a recent refinement run) reports this, which the new version should address:\n" + feedback.Trim() + "\n")}
        Current file:
        ```markdown
        {markdown.Trim()}
        ```
        In your Notes, say what you changed and why.
        """;

    private async Task<LibraryProposal> RunAsync(string header, string task, ImportAs kind, AiLibrary? library, string? keepName, CancellationToken ct)
    {
        var response = await _client.SendAsync(new AiRequest
        {
            Purpose = "library-assist",
            System = Shared + "\n\n" + header,
            Messages = { AiMessage.User(task) },
            MaxTokens = 3000,
        }, ct);
        return Parse(response.Text, kind, library, keepName);
    }

    private static readonly Regex Fence = new(@"```(?:markdown|md|yaml)?[ \t]*\n(.*?)\n```", RegexOptions.Singleline | RegexOptions.Compiled);

    public static LibraryProposal Parse(string reply, ImportAs kind, AiLibrary? library, string? keepName = null)
    {
        // Take the fenced block that holds a header; the file body may itself contain small fences.
        string? file = null;
        string after = "";
        foreach (Match m in Fence.Matches(reply))
        {
            if (!m.Groups[1].Value.TrimStart().StartsWith("---")) continue;
            file = m.Groups[1].Value;
            after = reply[(m.Index + m.Length)..];
            break;
        }
        if (file == null)
        {
            int i = reply.IndexOf("---", StringComparison.Ordinal);
            if (i < 0) throw new AiException(AiErrorKind.BadResponse, "The AI didn't send back a usable draft. Try again, or describe it a little differently.");
            int notesAt = reply.LastIndexOf("\nNotes:", StringComparison.OrdinalIgnoreCase);
            file = notesAt > i ? reply[i..notesAt] : reply[i..];
            after = notesAt > i ? reply[notesAt..] : "";
            file = file.Replace("```", "");
        }

        var fm = Frontmatter.Parse(file);
        if (string.IsNullOrWhiteSpace(fm.Body))
            throw new AiException(AiErrorKind.BadResponse, "The AI's draft came back empty. Try again.");

        var notes = after.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ") || l.StartsWith("* ") || l.StartsWith("• "))
                         .Select(l => l[2..].Trim()).Where(l => l.Length > 0).Take(6).ToList();

        if (kind == ImportAs.Skill)
        {
            var s = Skill.FromFrontmatter(fm);
            if (keepName != null) s.Name = keepName;
            return new LibraryProposal { Skill = s, Notes = notes };
        }

        var a = Agent.FromFrontmatter(fm);
        if (keepName != null) a.Name = keepName;
        a.Provider = "";
        a.Model = "";
        var dropped = AgentRuntime.ClampTools(a);
        if (dropped.Count > 0) notes.Add($"Left out tools the app doesn't have: {string.Join(", ", dropped)}.");
        if (a.Role != AgentRole.General && a.Tools.Count > 0) a.Tools.Clear(); // designers and critics don't use tools
        if (library != null)
        {
            var missing = a.Skills.Where(n => library.FindSkill(n) == null).ToList();
            if (missing.Count > 0)
            {
                a.Skills = a.Skills.Except(missing).ToList();
                notes.Add($"Left out skills that don't exist yet: {string.Join(", ", missing)}.");
            }
        }
        return new LibraryProposal { Agent = a, Notes = notes };
    }
}
