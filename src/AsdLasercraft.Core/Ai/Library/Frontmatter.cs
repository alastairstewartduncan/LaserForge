using System.Text;

namespace AsdLasercraft.Core.Ai.Library;

/// <summary>
/// A markdown file with an optional YAML header between --- lines, the format used for skills and agents here and
/// by several other AI tools. Only the small YAML subset those files use is understood: "key: value", quoted
/// strings, inline lists [a, b], dash lists and | or &gt; block text. Anything else is kept as raw text.
/// </summary>
public sealed class Frontmatter
{
    /// <summary>Header values in file order: a string, or a list of strings.</summary>
    public List<KeyValuePair<string, object>> Values { get; } = new();
    public string Body { get; set; } = "";
    public bool HadHeader { get; private set; }

    public string? Get(string key)
    {
        foreach (var kv in Values)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return kv.Value is List<string> l ? string.Join(", ", l) : (string)kv.Value;
        return null;
    }

    /// <summary>A value as a list: a real list, or a comma-separated string.</summary>
    public List<string> GetList(string key)
    {
        foreach (var kv in Values)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return kv.Value is List<string> l
                    ? l.Where(s => s.Length > 0).ToList()
                    : ((string)kv.Value).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote).ToList();
        return new();
    }

    public bool Has(string key) => Values.Any(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public Frontmatter Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return this;
        Values.Add(new(key, value.Trim()));
        return this;
    }

    public Frontmatter SetList(string key, IEnumerable<string> items)
    {
        var list = items.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (list.Count > 0) Values.Add(new(key, list));
        return this;
    }

    public static Frontmatter Parse(string text)
    {
        var fm = new Frontmatter();
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');
        var lines = text.Split('\n');
        int start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0) start++;
        if (start >= lines.Length || lines[start].Trim() != "---") { fm.Body = text.Trim(); return fm; }
        int end = -1;
        for (int i = start + 1; i < lines.Length; i++)
            if (lines[i].Trim() is "---" or "...") { end = i; break; }
        if (end < 0) { fm.Body = text.Trim(); return fm; }

        fm.HadHeader = true;
        fm.Body = string.Join("\n", lines.Skip(end + 1)).Trim();

        for (int i = start + 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (char.IsWhiteSpace(line[0])) continue; // stray nested line: not something we understand
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();

            if (rest is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var block = new List<string>();
                while (i + 1 < end && (lines[i + 1].Trim().Length == 0 || char.IsWhiteSpace(lines[i + 1][0]))) block.Add(lines[++i]);
                int indent = block.Where(b => b.Trim().Length > 0).Select(b => b.Length - b.TrimStart().Length).DefaultIfEmpty(0).Min();
                var cleaned = block.Select(b => b.Length >= indent ? b[indent..] : b.Trim());
                fm.Values.Add(new(key, (rest[0] == '>' ? string.Join(" ", cleaned.Select(c => c.Trim())) : string.Join("\n", cleaned)).Trim()));
            }
            else if (rest.Length == 0)
            {
                // A dash list, or a nested map (which is skipped).
                var items = new List<string>();
                bool nested = false;
                while (i + 1 < end && (lines[i + 1].Trim().Length == 0 || char.IsWhiteSpace(lines[i + 1][0]) || lines[i + 1].TrimStart().StartsWith("- ")))
                {
                    var t = lines[++i].Trim();
                    if (t.Length == 0) continue;
                    if (t.StartsWith("- ") || t == "-") items.Add(Unquote(t.Length > 1 ? t[2..].Trim() : ""));
                    else nested = true;
                }
                if (items.Count > 0 && !nested) fm.Values.Add(new(key, items));
                else if (!nested) fm.Values.Add(new(key, ""));
            }
            else if (rest.StartsWith('[') && rest.EndsWith(']'))
            {
                fm.Values.Add(new(key, SplitInline(rest[1..^1])));
            }
            else
            {
                fm.Values.Add(new(key, Unquote(StripComment(rest))));
            }
        }
        return fm;
    }

    private static List<string> SplitInline(string s)
    {
        var items = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (char c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); }
            else if (c is '"' or '\'') quote = c;
            else if (c == ',') { items.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        if (sb.ToString().Trim().Length > 0) items.Add(sb.ToString().Trim());
        return items.Where(i => i.Length > 0).ToList();
    }

    private static string StripComment(string s)
    {
        if (s.StartsWith('"') || s.StartsWith('\'')) return s;
        int i = s.IndexOf(" #", StringComparison.Ordinal);
        return i >= 0 ? s[..i].TrimEnd() : s;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        return s;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Values.Count > 0)
        {
            sb.Append("---\n");
            foreach (var (key, value) in Values)
            {
                if (value is List<string> list) sb.Append(key).Append(": [").Append(string.Join(", ", list.Select(Quote))).Append("]\n");
                else sb.Append(key).Append(": ").Append(Quote((string)value)).Append('\n');
            }
            sb.Append("---\n\n");
        }
        sb.Append(Body.Replace("\r\n", "\n").Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>Quotes a value only when YAML would otherwise misread it.</summary>
    private static string Quote(string s)
    {
        s = s.Replace("\r", "").Trim();
        bool plain = s.Length > 0 && !s.Contains('\n') && !s.Contains(": ") && !s.Contains(" #") && !s.EndsWith(':') &&
                     "-?:,[]{}#&*!|>'\"%@`".IndexOf(s[0]) < 0 && !s.Contains('"') &&
                     !(s is "true" or "false" or "null" or "yes" or "no" or "~");
        return plain ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }
}
