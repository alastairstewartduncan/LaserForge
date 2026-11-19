using System.Text;
using System.Text.RegularExpressions;

namespace AsdLasercraft.Core.Ai.Library;

/// <summary>What an agent is for. Designers draw, critics review a design, general agents run the assistant chat.</summary>
public enum AgentRole { General, Designer, Critic }

/// <summary>A reusable pack of instructions: a house style, a material, a product type.</summary>
public sealed class Skill
{
    /// <summary>Short id, lower-case with hyphens (also the file name).</summary>
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>One or two sentences saying when to use it.</summary>
    public string Description { get; set; } = "";
    public string Body { get; set; } = "";
    public bool BuiltIn { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title;
    public Skill Clone() => (Skill)MemberwiseClone();
    public override string ToString() => DisplayName;

    public string ToMarkdown() => new Frontmatter { Body = Body }
        .Set("name", Name).Set("title", Title).Set("description", Description).ToString();

    public static Skill FromFrontmatter(Frontmatter fm, string fallbackName = "skill")
    {
        var title = fm.Get("title") ?? "";
        var rawName = fm.Get("name") ?? "";
        // Some tools put a display name in "name": keep it as the title and derive the id from it.
        if (title.Length == 0 && rawName.Length > 0 && rawName != AiLibrary.Slug(rawName)) title = rawName;
        if (title.Length == 0 && rawName.Length == 0) title = AiLibrary.FirstHeading(fm.Body) ?? "";
        return new Skill
        {
            Name = AiLibrary.Slug(rawName.Length > 0 ? rawName : title.Length > 0 ? title : fallbackName),
            Title = title,
            Description = fm.Get("description") ?? "",
            Body = fm.Body,
        };
    }
}

/// <summary>A named role for the AI: instructions, a model, the tools it may use and the skills it draws on.</summary>
public sealed class Agent
{
    public const string AllTools = "*";

    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public AgentRole Role { get; set; } = AgentRole.General;
    public string Instructions { get; set; } = "";
    /// <summary>Provider id (see AiProviderCatalog); empty = whichever provider is active.</summary>
    public string Provider { get; set; } = "";
    /// <summary>Model id; empty = the provider's chosen model.</summary>
    public string Model { get; set; } = "";
    /// <summary>Assistant tools this agent may use: tool names, or "*" for all. Empty = advice only.</summary>
    public List<string> Tools { get; set; } = new();
    /// <summary>Names of the skills attached to this agent.</summary>
    public List<string> Skills { get; set; } = new();
    public bool BuiltIn { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title;
    public bool HasAllTools => Tools.Contains(AllTools);
    public override string ToString() => DisplayName;

    public Agent Clone()
    {
        var a = (Agent)MemberwiseClone();
        a.Tools = Tools.ToList();
        a.Skills = Skills.ToList();
        return a;
    }

    public string ToMarkdown() => new Frontmatter { Body = Instructions }
        .Set("name", Name).Set("title", Title).Set("description", Description)
        .Set("role", Role.ToString().ToLowerInvariant())
        .Set("provider", Provider).Set("model", Model)
        .SetList("tools", Tools).SetList("skills", Skills).ToString();

    public static Agent FromFrontmatter(Frontmatter fm, string fallbackName = "agent")
    {
        var title = fm.Get("title") ?? "";
        var rawName = fm.Get("name") ?? "";
        if (title.Length == 0 && rawName.Length > 0 && rawName != AiLibrary.Slug(rawName)) title = rawName;
        if (title.Length == 0 && rawName.Length == 0) title = AiLibrary.FirstHeading(fm.Body) ?? "";
        return new Agent
        {
            Name = AiLibrary.Slug(rawName.Length > 0 ? rawName : title.Length > 0 ? title : fallbackName),
            Title = title,
            Description = fm.Get("description") ?? "",
            Role = Enum.TryParse<AgentRole>(fm.Get("role"), true, out var r) ? r : AgentRole.General,
            Instructions = fm.Body,
            Provider = fm.Get("provider") ?? "",
            Model = fm.Get("model") ?? "",
            Tools = fm.GetList("tools"),
            Skills = fm.GetList("skills").Select(AiLibrary.Slug).ToList(),
        };
    }
}

/// <summary>
/// The user's skills and agents. Each is a plain markdown file in a folder (skills\name.md, agents\name.md), so they
/// can be read, edited, backed up and shared without the app. A few starters are built in and can be edited or hidden.
/// </summary>
public sealed class AiLibrary
{
    public const int MaxBodyChars = 60_000;

    public List<Skill> Skills { get; } = new();
    public List<Agent> Agents { get; } = new();
    /// <summary>Names of built-in items the user deleted, so they don't come back.</summary>
    public HashSet<string> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Skill? FindSkill(string? name) => string.IsNullOrWhiteSpace(name) ? null
        : Skills.FirstOrDefault(s => s.Name.Equals(Slug(name), StringComparison.OrdinalIgnoreCase))
          ?? Skills.FirstOrDefault(s => s.DisplayName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public Agent? FindAgent(string? name) => string.IsNullOrWhiteSpace(name) ? null
        : Agents.FirstOrDefault(a => a.Name.Equals(Slug(name), StringComparison.OrdinalIgnoreCase))
          ?? Agents.FirstOrDefault(a => a.DisplayName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Agent> AgentsFor(AgentRole role) => Agents.Where(a => a.Role == role);

    /// <summary>The skills an agent has attached that still exist.</summary>
    public List<Skill> SkillsOf(Agent? agent) =>
        agent == null ? new() : agent.Skills.Select(FindSkill).Where(s => s != null).Select(s => s!).Distinct().ToList();

    /// <summary>Adds an item, renaming it (name-2, name-3…) if the name is taken. Returns the final name.</summary>
    public string Add(Skill s)
    {
        s.Name = UniqueName(Slug(s.Name), n => Skills.Any(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        s.Body = Cap(s.Body);
        Skills.Add(s);
        return s.Name;
    }

    public string Add(Agent a)
    {
        a.Name = UniqueName(Slug(a.Name), n => Agents.Any(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        a.Instructions = Cap(a.Instructions);
        Agents.Add(a);
        return a.Name;
    }

    public void Remove(Skill s)
    {
        Skills.Remove(s);
        if (BuiltIns.Skills().Any(b => b.Name == s.Name)) Hidden.Add("skill:" + s.Name);
        foreach (var a in Agents) a.Skills.RemoveAll(n => n.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
    }

    public void Remove(Agent a)
    {
        Agents.Remove(a);
        if (BuiltIns.Agents().Any(b => b.Name == a.Name)) Hidden.Add("agent:" + a.Name);
    }

    private static string UniqueName(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        for (int i = 2; ; i++) if (!taken($"{name}-{i}")) return $"{name}-{i}";
    }

    private static string Cap(string s) => s.Length > MaxBodyChars ? s[..MaxBodyChars] : s;

    /// <summary>Lower-case, hyphenated, at most 64 characters: safe as a file name and accepted by other tools.</summary>
    public static string Slug(string? text)
    {
        var s = Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 64) s = s[..64].Trim('-');
        return s.Length == 0 ? "untitled" : s;
    }

    public static string? FirstHeading(string body)
    {
        var m = Regex.Match(body ?? "", @"^\s{0,3}#{1,3}\s+(.+?)\s*#*\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ------------------------------------------------------------------ storage

    /// <summary>Loads the library from a folder, adding the built-in starters the user hasn't replaced or deleted.</summary>
    public static AiLibrary Load(string folder)
    {
        var lib = new AiLibrary();
        try
        {
            var hidden = Path.Combine(folder, "hidden.txt");
            if (File.Exists(hidden))
                foreach (var line in File.ReadAllLines(hidden)) if (line.Trim().Length > 0) lib.Hidden.Add(line.Trim());
            foreach (var file in SafeFiles(Path.Combine(folder, "skills")))
            {
                var s = Skill.FromFrontmatter(Frontmatter.Parse(File.ReadAllText(file)), Path.GetFileNameWithoutExtension(file));
                if (lib.Skills.All(x => x.Name != s.Name)) lib.Skills.Add(s);
            }
            foreach (var file in SafeFiles(Path.Combine(folder, "agents")))
            {
                var a = Agent.FromFrontmatter(Frontmatter.Parse(File.ReadAllText(file)), Path.GetFileNameWithoutExtension(file));
                if (lib.Agents.All(x => x.Name != a.Name)) lib.Agents.Add(a);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable library folder just means starting from the built-ins.
        }
        lib.AddBuiltIns();
        return lib;
    }

    private static IEnumerable<string> SafeFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Array.Empty<string>();

    public void AddBuiltIns()
    {
        foreach (var s in BuiltIns.Skills())
            if (!Hidden.Contains("skill:" + s.Name) && Skills.All(x => x.Name != s.Name)) Skills.Add(s);
        foreach (var a in BuiltIns.Agents())
            if (!Hidden.Contains("agent:" + a.Name) && Agents.All(x => x.Name != a.Name)) Agents.Add(a);
        Skills.Sort((x, y) => string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase));
        Agents.Sort((x, y) => string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes the library to a folder. Untouched built-ins are not written, so app updates can improve them.</summary>
    public void Save(string folder)
    {
        var skillsDir = Path.Combine(folder, "skills");
        var agentsDir = Path.Combine(folder, "agents");
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(agentsDir);
        var builtInSkills = BuiltIns.Skills().ToDictionary(s => s.Name, s => s.ToMarkdown());
        var builtInAgents = BuiltIns.Agents().ToDictionary(a => a.Name, a => a.ToMarkdown());

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Skills)
        {
            var md = s.ToMarkdown();
            if (builtInSkills.TryGetValue(s.Name, out var original) && original == md) continue;
            var path = Path.Combine(skillsDir, s.Name + ".md");
            File.WriteAllText(path, md, new UTF8Encoding(false));
            keep.Add(path);
        }
        foreach (var a in Agents)
        {
            var md = a.ToMarkdown();
            if (builtInAgents.TryGetValue(a.Name, out var original) && original == md) continue;
            var path = Path.Combine(agentsDir, a.Name + ".md");
            File.WriteAllText(path, md, new UTF8Encoding(false));
            keep.Add(path);
        }
        foreach (var file in SafeFiles(skillsDir).Concat(SafeFiles(agentsDir)))
            if (!keep.Contains(file)) File.Delete(file);
        File.WriteAllLines(Path.Combine(folder, "hidden.txt"), Hidden.OrderBy(h => h));
    }
}
