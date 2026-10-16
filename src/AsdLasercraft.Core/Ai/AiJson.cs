using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AsdLasercraft.Core.Ai;

/// <summary>Helpers for reading structured answers out of model text and building tool schemas.</summary>
public static class AiJson
{
    private static readonly Regex Fence = new(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Finds the JSON object in a reply: a ```json fence if present, otherwise the outermost {…}.</summary>
    public static JsonObject? ExtractObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Fence.Match(text);
        var candidates = new List<string>();
        if (m.Success) candidates.Add(m.Groups[1].Value);
        int first = text.IndexOf('{'), last = text.LastIndexOf('}');
        if (first >= 0 && last > first) candidates.Add(text[first..(last + 1)]);
        foreach (var c in candidates)
        {
            try { if (JsonNode.Parse(c) is JsonObject o) return o; }
            catch (JsonException) { }
        }
        return null;
    }

    public static string? Str(this JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[name]?.ToString();

    /// <summary>Reads a number that may arrive as a JSON number or a numeric string.</summary>
    public static double? Num(this JsonObject o, string name)
    {
        if (o[name] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public static int? Int(this JsonObject o, string name) => o.Num(name) is { } d ? (int)Math.Round(d) : null;

    public static bool? Bool(this JsonObject o, string name)
    {
        if (o[name] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        return null;
    }

    // ------------------------------------------------------------------ schema builders

    public static JsonObject Schema(params (string Name, JsonObject Prop, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, prop, req) in props)
        {
            properties[name] = prop;
            if (req) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    public static JsonObject StringProp(string description, params string[] allowed)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (allowed.Length > 0) o["enum"] = new JsonArray(allowed.Select(a => (JsonNode)a).ToArray());
        return o;
    }

    public static JsonObject NumberProp(string description) => new() { ["type"] = "number", ["description"] = description };
    public static JsonObject IntProp(string description) => new() { ["type"] = "integer", ["description"] = description };
    public static JsonObject BoolProp(string description) => new() { ["type"] = "boolean", ["description"] = description };
}
