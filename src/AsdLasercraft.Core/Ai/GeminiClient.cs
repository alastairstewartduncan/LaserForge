using System.Text.Json.Nodes;

namespace AsdLasercraft.Core.Ai;

/// <summary>Talks to Google's Gemini API (generateContent) with the user's own API key.</summary>
public sealed class GeminiClient : IAiClient, IDisposable
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string Model { get; set; } = "";

    public GeminiClient(Func<string?> apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _ownsHttp = http == null;
        _http = http ?? AiHttp.NewClient();
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey());

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new AiException(AiErrorKind.NoKey, "No Gemini API key has been set up yet. Add one in AI → AI settings.");
        var model = (request.Model ?? Model).Trim();
        if (model.Length == 0)
            throw new AiException(AiErrorKind.BadRequest, "No Gemini model has been chosen. Pick one in AI → AI settings.");
        if (model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) model = model[7..];

        Uri uri;
        try { uri = new Uri($"{(string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl.Trim().TrimEnd('/'))}/models/{Uri.EscapeDataString(model)}:generateContent"); }
        catch (UriFormatException) { throw new AiException(AiErrorKind.BadRequest, "The Gemini address isn't a valid URL. Check it in AI → AI settings."); }

        using var msg = AiHttp.JsonPost(uri, BuildBody(request));
        msg.Headers.Add("x-goog-api-key", key.Trim());
        var (status, text) = await AiHttp.SendAsync(_http, msg, cancellationToken).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300)
        {
            // Gemini reports a bad key as 400 INVALID_ARGUMENT.
            if ((int)status == 400 && text.Contains("API key", StringComparison.OrdinalIgnoreCase))
                throw new AiException(AiErrorKind.BadKey, "The Gemini API key was rejected. Check it in AI → AI settings.");
            throw ClaudeClient.ErrorFor(status, text, "Gemini");
        }
        return AiHttp.Parse(() => ParseResponse(text));
    }

    public static JsonObject BuildBody(AiRequest request)
    {
        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.System))
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.System }) };

        // Function responses are matched by name, so remember which call each ID belonged to.
        var callNames = request.Messages.SelectMany(m => m.Content.OfType<AiToolUse>())
                               .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);

        var contents = new JsonArray();
        foreach (var m in request.Messages)
        {
            var parts = new JsonArray();
            foreach (var c in m.Content)
            {
                switch (c)
                {
                    case AiText t when t.Text.Length > 0:
                        parts.Add(new JsonObject { ["text"] = t.Text });
                        break;
                    case AiImage i:
                        parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = i.MediaType, ["data"] = Convert.ToBase64String(i.Data) } });
                        break;
                    case AiToolUse u:
                    {
                        var call = new JsonObject { ["name"] = u.Name, ["args"] = u.Input.DeepClone() };
                        if (u.ProviderData?["id"] is { } id) call["id"] = id.DeepClone();
                        var part = new JsonObject { ["functionCall"] = call };
                        if (u.ProviderData?["thoughtSignature"] is { } sig) part["thoughtSignature"] = sig.DeepClone();
                        parts.Add(part);
                        break;
                    }
                    case AiToolResult r:
                    {
                        var resp = new JsonObject
                        {
                            ["name"] = callNames.TryGetValue(r.ToolUseId, out var name) ? name : "tool",
                            ["response"] = new JsonObject { [r.IsError ? "error" : "result"] = r.Content },
                        };
                        var original = request.Messages.SelectMany(x => x.Content.OfType<AiToolUse>()).FirstOrDefault(x => x.Id == r.ToolUseId);
                        if (original?.ProviderData?["id"] is { } id) resp["id"] = id.DeepClone();
                        parts.Add(new JsonObject { ["functionResponse"] = resp });
                        break;
                    }
                }
            }
            if (parts.Count == 0) continue;
            contents.Add(new JsonObject { ["role"] = m.Role == AiRole.User ? "user" : "model", ["parts"] = parts });
        }
        body["contents"] = contents;

        if (request.Tools.Count > 0)
        {
            var decls = new JsonArray();
            foreach (var t in request.Tools)
            {
                var d = new JsonObject { ["name"] = t.Name, ["description"] = t.Description };
                if (ToGeminiSchema(t.InputSchema) is { } schema) d["parameters"] = schema;
                decls.Add(d);
            }
            body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = decls });
        }

        // The limit covers the model's thinking as well as its answer, so leave room for both.
        body["generationConfig"] = new JsonObject { ["maxOutputTokens"] = Math.Max(request.MaxTokens * 2, request.MaxTokens + 2000) };
        return body;
    }

    /// <summary>
    /// Converts a JSON Schema object to Gemini's schema dialect: upper-case type names, no empty "required",
    /// and no parameters at all for a tool that takes none (returns null).
    /// </summary>
    public static JsonObject? ToGeminiSchema(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject props || props.Count == 0) return null;
        return (JsonObject)Convert(schema)!;

        static JsonNode? Convert(JsonNode? node)
        {
            if (node is JsonArray arr) return new JsonArray(arr.Select(Convert).ToArray());
            if (node is not JsonObject o) return node?.DeepClone();
            var result = new JsonObject();
            foreach (var (k, v) in o)
            {
                if (k == "required" && v is JsonArray { Count: 0 }) continue;
                if (k == "type" && v is JsonValue tv && tv.TryGetValue<string>(out var ts)) { result[k] = ts.ToUpperInvariant(); continue; }
                if (k == "properties" && v is JsonObject p)
                {
                    var np = new JsonObject();
                    foreach (var (pk, pv) in p) np[pk] = Convert(pv);
                    result[k] = np;
                    continue;
                }
                result[k] = k is "items" ? Convert(v) : v?.DeepClone();
            }
            return result;
        }
    }

    public static AiResponse ParseResponse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var candidate = root["candidates"]?.AsArray().FirstOrDefault() as JsonObject;
        if (candidate == null)
        {
            var reason = root["promptFeedback"]?["blockReason"]?.ToString();
            throw new AiException(AiErrorKind.BadResponse, reason != null
                ? $"Gemini declined the request ({reason}). Try wording it differently."
                : "Gemini didn't send an answer. Try again.");
        }

        var content = new List<AiContent>();
        int n = 0;
        foreach (var part in (candidate["content"]?["parts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (part["thought"] is JsonValue th && th.TryGetValue<bool>(out var isThought) && isThought) continue;
            if (part["functionCall"] is JsonObject fc)
            {
                var name = fc["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var data = new JsonObject();
                if (part["thoughtSignature"] is { } sig) data["thoughtSignature"] = sig.DeepClone();
                if (fc["id"] is { } id) data["id"] = id.DeepClone();
                content.Add(new AiToolUse($"gemini_{n++}_{Guid.NewGuid():N}", name,
                    fc["args"] is JsonObject args ? (JsonObject)args.DeepClone() : new JsonObject(), data.Count > 0 ? data : null));
            }
            else if (part["text"] is JsonValue tv && tv.TryGetValue<string>(out var text) && text.Length > 0)
            {
                content.Add(new AiText(text));
            }
        }

        bool hasCalls = content.OfType<AiToolUse>().Any();
        var finish = candidate["finishReason"]?.ToString() ?? "STOP";
        var usage = root["usageMetadata"];
        return new AiResponse
        {
            Content = content,
            StopReason = hasCalls ? "tool_use" : finish == "MAX_TOKENS" ? "max_tokens" : "end_turn",
            Usage = new AiUsage(OpenAiCompatibleClient.IntOf(usage?["promptTokenCount"]), OpenAiCompatibleClient.IntOf(usage?["candidatesTokenCount"])),
        };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
