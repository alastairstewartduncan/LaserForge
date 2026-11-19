using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AsdLasercraft.Core.Ai;

/// <summary>
/// Talks to any service that speaks the OpenAI Chat Completions format: OpenAI itself, OpenRouter, and local servers
/// such as Ollama and LM Studio. Only the base URL, key and model differ.
/// </summary>
public sealed class OpenAiCompatibleClient : IAiClient, IDisposable
{
    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string BaseUrl { get; set; }
    public string Model { get; set; } = "";
    /// <summary>Used in error messages, e.g. "OpenAI" or "Ollama".</summary>
    public string ProviderName { get; set; } = "AI";
    /// <summary>Local servers need no key.</summary>
    public bool KeyRequired { get; set; } = true;
    /// <summary>Newer OpenAI models want max_completion_tokens; most compatible servers still want max_tokens. Corrected automatically if the server objects.</summary>
    public bool UseMaxCompletionTokens { get; set; }

    public OpenAiCompatibleClient(Func<string?> apiKey, string baseUrl, HttpClient? http = null)
    {
        _apiKey = apiKey;
        BaseUrl = baseUrl;
        _ownsHttp = http == null;
        _http = http ?? AiHttp.NewClient();
    }

    public bool IsConfigured => (!KeyRequired || !string.IsNullOrWhiteSpace(_apiKey())) && !string.IsNullOrWhiteSpace(BaseUrl);

    public static Uri EndpointFor(string baseUrl, string path) => new(baseUrl.Trim().TrimEnd('/') + "/" + path);

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var key = _apiKey();
        if (KeyRequired && string.IsNullOrWhiteSpace(key))
            throw new AiException(AiErrorKind.NoKey, $"No {ProviderName} API key has been set up yet. Add one in AI → AI settings.");
        var model = request.Model ?? Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new AiException(AiErrorKind.BadRequest, $"No model has been chosen for {ProviderName}. Pick one in AI → AI settings.");
        Uri uri;
        try { uri = EndpointFor(BaseUrl, "chat/completions"); }
        catch (UriFormatException) { throw new AiException(AiErrorKind.BadRequest, $"The address for {ProviderName} isn't a valid URL. Check it in AI → AI settings."); }

        for (int attempt = 0; ; attempt++)
        {
            using var msg = AiHttp.JsonPost(uri, BuildBody(request, model, UseMaxCompletionTokens));
            if (!string.IsNullOrWhiteSpace(key)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var (status, text) = await AiHttp.SendAsync(_http, msg, cancellationToken).ConfigureAwait(false);
            if ((int)status is >= 200 and < 300) return AiHttp.Parse(() => ParseResponse(text));

            // Some models only accept one of the two token-limit names: switch and try once more.
            if (attempt == 0 && (int)status == 400 && text.Contains(UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                UseMaxCompletionTokens = !UseMaxCompletionTokens;
                continue;
            }
            throw ClaudeClient.ErrorFor(status, text, ProviderName);
        }
    }

    public static JsonObject BuildBody(AiRequest request, string model, bool useMaxCompletionTokens)
    {
        var body = new JsonObject { ["model"] = model };
        // Reasoning models spend part of this budget thinking, so give them room on top of the visible answer.
        if (useMaxCompletionTokens) body["max_completion_tokens"] = Math.Max(request.MaxTokens * 2, request.MaxTokens + 2000);
        else body["max_tokens"] = request.MaxTokens;

        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });

        foreach (var m in request.Messages)
        {
            // Tool results are their own messages in this format, and must come straight after the assistant's calls.
            foreach (var r in m.Content.OfType<AiToolResult>())
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = r.ToolUseId,
                    ["content"] = r.IsError ? "ERROR: " + r.Content : r.Content,
                });

            var texts = m.Content.OfType<AiText>().Where(t => t.Text.Length > 0).ToList();
            var images = m.Content.OfType<AiImage>().ToList();
            var calls = m.Content.OfType<AiToolUse>().ToList();

            if (m.Role == AiRole.Assistant)
            {
                if (texts.Count == 0 && calls.Count == 0) continue;
                var a = new JsonObject { ["role"] = "assistant" };
                a["content"] = texts.Count > 0 ? string.Join("\n", texts.Select(t => t.Text)) : null;
                if (calls.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var c in calls)
                    {
                        var call = new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Input.ToJsonString() },
                        };
                        if (c.ProviderData?["extra_content"] is { } extra) call["extra_content"] = extra.DeepClone();
                        arr.Add(call);
                    }
                    a["tool_calls"] = arr;
                }
                messages.Add(a);
            }
            else if (images.Count > 0)
            {
                var parts = new JsonArray();
                foreach (var t in texts) parts.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                foreach (var i in images)
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{i.MediaType};base64,{Convert.ToBase64String(i.Data)}" },
                    });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
            }
            else if (texts.Count > 0)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", texts.Select(t => t.Text)) });
            }
        }
        body["messages"] = messages;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
                });
            body["tools"] = tools;
        }
        return body;
    }

    public static AiResponse ParseResponse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var choice = root["choices"]?.AsArray().FirstOrDefault() as JsonObject
                     ?? throw new InvalidOperationException("No choices in the response.");
        var message = choice["message"] as JsonObject ?? new JsonObject();
        var content = new List<AiContent>();

        switch (message["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0:
                content.Add(new AiText(s));
                break;
            case JsonArray parts: // some servers answer with content parts
                foreach (var p in parts.OfType<JsonObject>())
                    if (p["text"] is JsonValue pv && pv.TryGetValue<string>(out var ps) && ps.Length > 0) content.Add(new AiText(ps));
                break;
        }

        if (message["tool_calls"] is JsonArray calls)
        {
            int n = 0;
            foreach (var c in calls.OfType<JsonObject>())
            {
                var fn = c["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var id = c["id"] is JsonValue idv && idv.TryGetValue<string>(out var ids) && ids.Length > 0 ? ids : $"call_{n}_{Guid.NewGuid():N}";
                n++;
                JsonObject? data = c["extra_content"] is { } extra ? new JsonObject { ["extra_content"] = extra.DeepClone() } : null;
                content.Add(new AiToolUse(id, name, ParseArguments(fn?["arguments"]), data));
            }
        }

        bool hasCalls = content.OfType<AiToolUse>().Any();
        var finish = choice["finish_reason"] is JsonValue fv && fv.TryGetValue<string>(out var fs) ? fs : "stop";
        var usage = root["usage"];
        return new AiResponse
        {
            Content = content,
            StopReason = hasCalls ? "tool_use" : finish == "length" ? "max_tokens" : "end_turn",
            Usage = new AiUsage(IntOf(usage?["prompt_tokens"]), IntOf(usage?["completion_tokens"])),
        };
    }

    /// <summary>Arguments normally arrive as a JSON string; a few servers send an object. Anything unreadable becomes {}.</summary>
    private static JsonObject ParseArguments(JsonNode? node)
    {
        if (node is JsonObject o) return (JsonObject)o.DeepClone();
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            try { if (JsonNode.Parse(s) is JsonObject parsed) return parsed; }
            catch (JsonException) { }
        }
        return new JsonObject();
    }

    internal static int IntOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 0;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
