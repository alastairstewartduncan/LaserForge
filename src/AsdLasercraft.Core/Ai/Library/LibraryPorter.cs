using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AsdLasercraft.Core.Ai.Library;

public enum ImportAs { Auto, Skill, Agent }

public enum ExportFormat
{
    /// <summary>The app's own markdown file with a header. Also readable by tools that use SKILL.md-style files.</summary>
    Markdown,
    /// <summary>A zip holding name/SKILL.md, the folder layout Claude skills use.</summary>
    SkillZip,
    /// <summary>Plain instructions to paste into any assistant's "custom instructions" box (attached skills included).</summary>
    PlainText,
    /// <summary>A small JSON object: name, description, instructions, model, tools.</summary>
    Json,
}

public sealed class ImportResult
{
    public List<Skill> Skills { get; } = new();
    public List<Agent> Agents { get; } = new();
    /// <summary>Things the user should know: what didn't carry over and what was switched off.</summary>
    public List<string> Notes { get; } = new();
    public bool IsEmpty => Skills.Count == 0 && Agents.Count == 0;
}

/// <summary>
/// Moves skills and agents in and out of the app. Imported files are treated as untrusted text: an imported agent
/// never arrives with tools switched on or a model chosen, and what it asked for is reported so the user can decide.
/// </summary>
public static class LibraryPorter
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxZipEntries = 400;

    // ================================================================== import

    public static ImportResult ImportFile(string path, ImportAs hint = ImportAs.Auto)
    {
        var info = new FileInfo(path);
        if (info.Length > 20 * 1024 * 1024) throw new InvalidDataException("That file is too big to be a skill or agent (over 20 MB).");
        var bytes = File.ReadAllBytes(path);
        return ImportBytes(Path.GetFileName(path), bytes, hint);
    }

    public static ImportResult ImportBytes(string fileName, byte[] bytes, ImportAs hint = ImportAs.Auto)
    {
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4) return ImportZip(bytes);
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("That file is too big to be a skill or agent (over 2 MB of text).");
        return ImportText(fileName, new UTF8Encoding(false).GetString(bytes), hint);
    }

    public static ImportResult ImportText(string fileName, string text, ImportAs hint = ImportAs.Auto)
    {
        var result = new ImportResult();
        text = text.TrimStart('﻿');
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                ImportJson(JsonNode.Parse(trimmed), fileName, hint, result);
                if (!result.IsEmpty) return result;
            }
            catch (JsonException) { /* not JSON after all: treat as text */ }
        }
        ImportMarkdown(fileName, text, hint, result);
        return result;
    }

    private static void ImportMarkdown(string fileName, string text, ImportAs hint, ImportResult result)
    {
        var fm = Frontmatter.Parse(text);
        if (string.IsNullOrWhiteSpace(fm.Body) && !fm.HadHeader) { result.Notes.Add($"{fileName}: nothing to import (the file is empty)."); return; }

        var baseName = BaseName(fileName);
        var kind = hint != ImportAs.Auto ? hint : GuessKind(fileName, fm);
        if (kind == ImportAs.Agent)
        {
            var a = Agent.FromFrontmatter(fm, baseName);
            if (!fm.Has("name") && !fm.Has("title") && AiLibrary.FirstHeading(fm.Body) == null) a.Title = Titled(baseName);
            Untrust(a, fileName, result);
            result.Agents.Add(a);
        }
        else
        {
            var s = Skill.FromFrontmatter(fm, baseName);
            if (!fm.Has("name") && !fm.Has("title") && AiLibrary.FirstHeading(fm.Body) == null) s.Title = Titled(baseName);
            if (fm.Has("allowed-tools") || fm.Has("tools"))
                result.Notes.Add($"\"{s.DisplayName}\": the tool permissions in the file were ignored. Skills here are instructions only.");
            result.Skills.Add(s);
        }
        var ignored = fm.Values.Select(v => v.Key.ToLowerInvariant())
            .Except(new[] { "name", "title", "description", "role", "provider", "model", "tools", "skills", "allowed-tools", "kind", "type" }).ToList();
        if (ignored.Count > 0)
            result.Notes.Add($"{fileName}: these settings have no equivalent here and were left out: {string.Join(", ", ignored)}.");
    }

    private static ImportAs GuessKind(string fileName, Frontmatter fm)
    {
        var kind = (fm.Get("kind") ?? fm.Get("type") ?? "").ToLowerInvariant();
        if (kind is "agent" or "subagent") return ImportAs.Agent;
        if (kind is "skill" or "rule") return ImportAs.Skill;

        var lower = fileName.Replace('\\', '/').ToLowerInvariant();
        if (lower.EndsWith("skill.md") || lower.Contains("/skills/") || lower.EndsWith(".mdc") || lower.EndsWith(".cursorrules") ||
            lower.EndsWith("agents.md") || lower.EndsWith("claude.md") || lower.EndsWith("copilot-instructions.md") || lower.EndsWith(".instructions.md"))
            return ImportAs.Skill;
        if (lower.EndsWith(".agent.md") || lower.EndsWith(".chatmode.md") || lower.Contains("/agents/")) return ImportAs.Agent;
        if (fm.Has("role") || fm.Has("tools") || fm.Has("model") || fm.Has("skills") || fm.Has("provider")) return ImportAs.Agent;
        if (fm.HadHeader) return ImportAs.Skill;

        // Bare text: a system prompt usually addresses the model directly.
        var start = fm.Body.TrimStart().ToLowerInvariant();
        return start.StartsWith("you are") || start.StartsWith("act as") || start.StartsWith("your role") ? ImportAs.Agent : ImportAs.Skill;
    }

    /// <summary>Reads the common JSON shapes for assistants: an object (or array of objects) with a name and instructions.</summary>
    private static void ImportJson(JsonNode? node, string fileName, ImportAs hint, ImportResult result)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr) ImportJson(item, fileName, hint, result);
            return;
        }
        if (node is not JsonObject o) return;

        // A bundle with separate lists.
        if (o["skills"] is JsonArray skills && skills.All(s => s is JsonObject)) foreach (var s in skills) ImportJson(s, fileName, ImportAs.Skill, result);
        if (o["agents"] is JsonArray agents) foreach (var a in agents) ImportJson(a, fileName, ImportAs.Agent, result);

        string? Pick(params string[] names) => names.Select(n => o[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        var instructions = Pick("instructions", "system_prompt", "systemPrompt", "system", "prompt", "body", "content", "system_message");
        if (instructions == null) return;
        var name = Pick("title", "display_name", "displayName", "name") ?? Titled(BaseName(fileName));
        var description = Pick("description", "summary") ?? "";

        var kind = hint != ImportAs.Auto ? hint : ImportAs.Agent;
        if (kind == ImportAs.Skill)
        {
            result.Skills.Add(new Skill { Name = AiLibrary.Slug(Pick("name") ?? name), Title = name, Description = description, Body = instructions });
            return;
        }
        var agent = new Agent
        {
            Name = AiLibrary.Slug(Pick("name") ?? name), Title = name, Description = description, Instructions = instructions,
            Model = Pick("model") ?? "",
            Role = Enum.TryParse<AgentRole>(Pick("role"), true, out var role) ? role : AgentRole.General,
        };
        if (o["tools"] is JsonArray tools)
            foreach (var t in tools)
            {
                var tn = t is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts
                    : t?["function"]?["name"]?.ToString() ?? t?["name"]?.ToString() ?? t?["type"]?.ToString();
                if (!string.IsNullOrWhiteSpace(tn)) agent.Tools.Add(tn);
            }
        if (o["skills"] is JsonArray sk && sk.All(s => s is JsonValue))
            agent.Skills.AddRange(sk.Select(s => AiLibrary.Slug(s!.ToString())));
        Untrust(agent, fileName, result);
        result.Agents.Add(agent);
    }

    /// <summary>
    /// Strips anything an imported agent shouldn't be able to decide for itself: its tools are switched off and its
    /// model choice cleared, with a note saying what it asked for.
    /// </summary>
    private static void Untrust(Agent a, string fileName, ImportResult result)
    {
        if (a.Tools.Count > 0)
        {
            var known = AgentRuntime.KnownToolNames();
            var here = a.Tools.Where(t => t == Agent.AllTools || known.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            var foreign = a.Tools.Except(here).ToList();
            var note = new StringBuilder($"\"{a.DisplayName}\" arrived with its tools switched off, so it can only give advice until you tick the tools you want it to have.");
            if (here.Count > 0) note.Append($" It asked for: {string.Join(", ", here.Select(t => t == Agent.AllTools ? "all tools" : t))}.");
            if (foreign.Count > 0) note.Append($" These tools from the other app don't exist here: {string.Join(", ", foreign.Take(12))}{(foreign.Count > 12 ? "…" : "")}.");
            result.Notes.Add(note.ToString());
            a.Tools.Clear();
        }
        if (a.Model.Length > 0 || a.Provider.Length > 0)
        {
            result.Notes.Add($"\"{a.DisplayName}\" named the model \"{(a.Model.Length > 0 ? a.Model : a.Provider)}\". It will use your current provider and model instead; you can change that in the editor.");
            a.Model = "";
            a.Provider = "";
        }
        if (a.Instructions.Length > AiLibrary.MaxBodyChars)
        {
            a.Instructions = a.Instructions[..AiLibrary.MaxBodyChars];
            result.Notes.Add($"\"{a.DisplayName}\" was very long and has been shortened.");
        }
    }

    private static ImportResult ImportZip(byte[] bytes)
    {
        var result = new ImportResult();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxZipEntries) throw new InvalidDataException("That archive has too many files to be a skill or agent pack.");

        var entries = zip.Entries.Where(e => e.Length > 0 && !e.FullName.Contains("__MACOSX")).ToList();
        var skillFolders = new List<string>();
        var imported = new HashSet<string>();
        foreach (var e in entries)
        {
            var full = e.FullName.Replace('\\', '/');
            var lower = full.ToLowerInvariant();
            bool isSkill = lower.EndsWith("/skill.md") || lower == "skill.md";
            bool isAgent = !isSkill && lower.EndsWith(".md") && (lower.Contains("agents/") || lower.EndsWith(".agent.md"));
            if (!isSkill && !isAgent) continue;
            if (e.Length > MaxFileBytes) { result.Notes.Add($"{full}: skipped (too big)."); continue; }
            var text = ReadEntry(e);
            var folder = full.Contains('/') ? full[..full.LastIndexOf('/')] : "";
            // A SKILL.md takes its fallback name from its folder.
            var name = isSkill ? (folder.Length > 0 ? folder[(folder.LastIndexOf('/') + 1)..] + "/SKILL.md" : "SKILL.md") : full;
            ImportMarkdown(name, text, isSkill ? ImportAs.Skill : ImportAs.Agent, result);
            imported.Add(full);
            if (isSkill) skillFolders.Add(folder.Length > 0 ? folder + "/" : "");
        }

        // Anything else inside a skill folder (scripts, reference files) can't be used here: say so.
        foreach (var folder in skillFolders.Distinct())
        {
            int extras = entries.Count(e => { var f = e.FullName.Replace('\\', '/'); return f.StartsWith(folder) && !imported.Contains(f); });
            if (extras > 0)
                result.Notes.Add($"{(folder.Length > 0 ? folder.TrimEnd('/') : "The skill")}: {extras} supporting file(s) (scripts, references, assets) were not imported. Skills here are instructions only, so anything the skill ran or read from those files won't happen.");
        }

        if (result.IsEmpty)
        {
            // No recognised layout: fall back to any single markdown or text file.
            var loose = entries.Where(e => e.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
            foreach (var e in loose.Where(e => e.Length <= MaxFileBytes)) ImportMarkdown(e.FullName, ReadEntry(e), ImportAs.Auto, result);
        }
        return result;
    }

    private static string ReadEntry(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read, total = 0;
        while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxFileBytes) throw new InvalidDataException("A file in the archive is larger than it claims to be.");
            ms.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false).GetString(ms.ToArray()).TrimStart('﻿');
    }

    private static string BaseName(string fileName)
    {
        var f = fileName.Replace('\\', '/');
        if (f.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)) return f[..^9].Split('/').Last();
        f = f.Split('/').Last();
        foreach (var ext in new[] { ".agent.md", ".chatmode.md", ".instructions.md", ".prompt.md", ".md", ".mdc", ".txt", ".json", ".cursorrules" })
            if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && f.Length > ext.Length) return f[..^ext.Length];
        return f.TrimStart('.');
    }

    private static string Titled(string slugOrName)
    {
        var s = slugOrName.Replace('-', ' ').Replace('_', ' ').Trim();
        return s.Length == 0 ? "Imported" : char.ToUpperInvariant(s[0]) + s[1..];
    }

    // ================================================================== export

    public sealed record Exported(string FileName, byte[] Bytes);

    public static Exported Export(Skill s, ExportFormat format)
    {
        switch (format)
        {
            case ExportFormat.SkillZip:
                return new Exported(s.Name + ".zip", Zip((s.Name + "/SKILL.md", PortableSkillMarkdown(s))));
            case ExportFormat.PlainText:
                return Text(s.Name + ".txt", $"{s.DisplayName}\n{s.Description}\n\n{s.Body.Trim()}\n");
            case ExportFormat.Json:
                return Text(s.Name + ".json", new JsonObject
                {
                    ["kind"] = "skill", ["name"] = s.Name, ["title"] = s.DisplayName, ["description"] = s.Description, ["instructions"] = s.Body.Trim(),
                }.ToJsonString(Indented));
            default:
                return Text(s.Name + ".md", s.ToMarkdown());
        }
    }

    public static Exported Export(Agent a, ExportFormat format, AiLibrary library)
    {
        var skills = library.SkillsOf(a);
        switch (format)
        {
            case ExportFormat.SkillZip:
            {
                // The agent file plus the skills it uses, in the agents/ and skills/ layout.
                var files = new List<(string, string)> { ($"agents/{a.Name}.md", a.ToMarkdown()) };
                files.AddRange(skills.Select(s => ($"skills/{s.Name}/SKILL.md", PortableSkillMarkdown(s))));
                return new Exported(a.Name + ".zip", Zip(files.ToArray()));
            }
            case ExportFormat.PlainText:
            {
                var sb = new StringBuilder();
                sb.AppendLine(a.DisplayName);
                if (a.Description.Length > 0) sb.AppendLine(a.Description);
                sb.AppendLine().AppendLine(AgentRuntime.Compose(a, library));
                return Text(a.Name + ".txt", sb.ToString());
            }
            case ExportFormat.Json:
                return Text(a.Name + ".json", new JsonObject
                {
                    ["kind"] = "agent", ["name"] = a.Name, ["title"] = a.DisplayName, ["description"] = a.Description,
                    ["role"] = a.Role.ToString().ToLowerInvariant(),
                    ["instructions"] = AgentRuntime.Compose(a, library),
                    ["model"] = a.Model,
                    ["tools"] = new JsonArray(a.Tools.Select(t => (JsonNode)t).ToArray()),
                }.ToJsonString(Indented));
            default:
                return Text(a.Name + ".md", a.ToMarkdown());
        }
    }

    /// <summary>Everything in one zip (skills/name/SKILL.md and agents/name.md), for backup or moving to another PC.</summary>
    public static Exported ExportAll(AiLibrary library)
    {
        var files = library.Skills.Select(s => ($"skills/{s.Name}/SKILL.md", PortableSkillMarkdown(s)))
            .Concat(library.Agents.Select(a => ($"agents/{a.Name}.md", a.ToMarkdown()))).ToArray();
        return new Exported("lasercraft-skills-and-agents.zip", Zip(files));
    }

    /// <summary>SKILL.md with only the header fields other tools expect (name and description).</summary>
    private static string PortableSkillMarkdown(Skill s)
    {
        var description = s.Description.Length > 0 ? s.Description : s.DisplayName;
        var body = s.Body.Trim();
        if (AiLibrary.FirstHeading(body) == null) body = $"# {s.DisplayName}\n\n{body}";
        return new Frontmatter { Body = body }.Set("name", s.Name).Set("description", description).ToString();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static Exported Text(string name, string text) => new(name, new UTF8Encoding(false).GetBytes(text));

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in files)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }
}
