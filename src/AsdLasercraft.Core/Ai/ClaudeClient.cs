using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AsdLasercraft.Core.Ai;

/// <summary>
/// Talks to the Claude Messages API (https://api.anthropic.com/v1/messages) with the user's own API key.
/// Request/response JSON is built and parsed here by hand so the core keeps zero package dependencies.
/// </summary>
public sealed class ClaudeClient : IAiClient, IDisposable
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string FastModel = "claude-haiku-5-5";
    public const string ApiVersion = "2023-06-01";
    public static readonly Uri Endpoint = new("https://api.anthropic.com/v1/messages");

    /// <summary>Models offered in AI settings. The user can also type any other model ID.</summary>
    public static readonly IReadOnlyList<string> SuggestedModels = new[] { DefaultModel, FastModel, "claude-opus-5-5" };

    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string Model { get; set; } = DefaultModel;

    public ClaudeClient(Func<string?> apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey());

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new AiException(AiErrorKind.NoKey, "No Claude API key has been set up yet. Add one in AI → AI settings.");

        var body = BuildBody(request, request.Model ?? Model);
        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-api-key", key.Trim());
        msg.Headers.Add("anthropic-version", ApiVersion);
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new AiException(AiErrorKind.Cancelled, "Cancelled.");
        }
        catch (TaskCanceledException ex)
        {
            throw new AiException(AiErrorKind.Network, "The AI service took too long to answer. Check your internet connection and try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiException(AiErrorKind.Network, "Couldn't reach the AI service. Check your internet connection and try again.", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw ErrorFor(resp.StatusCode, text);
            try { return ParseResponse(text); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new AiException(AiErrorKind.BadResponse, "The AI service sent an answer the app couldn't read.", ex);
            }
        }
    }

    /// <summary>Turns an HTTP failure into a message a user can act on.</summary>
    public static AiException ErrorFor(HttpStatusCode status, string body)
    {
        string detail = "";
        try { detail = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? ""; } catch (JsonException) { }
        return (int)status switch
        {
            401 or 403 => new AiException(AiErrorKind.BadKey, "The Claude API key was rejected. Check it in AI → AI settings."),
            429 => new AiException(AiErrorKind.RateLimited, "Too many AI requests in a short time (or the account is out of credit). Wait a moment and try again."),
            529 => new AiException(AiErrorKind.Overloaded, "The AI service is busy right now. Try again in a minute."),
            >= 500 => new AiException(AiErrorKind.Server, "The AI service had a problem. Try again in a minute."),
            404 => new AiException(AiErrorKind.BadRequest, "The AI model wasn't found. Pick a different model in AI → AI settings." + Suffix(detail)),
            _ => new AiException(AiErrorKind.BadRequest, "The AI service refused the request." + Suffix(detail)),
        };

        static string Suffix(string d) => string.IsNullOrWhiteSpace(d) ? "" : $" ({d})";
    }

    public static JsonObject BuildBody(AiRequest request, string model)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxTokens,
        };
        if (!string.IsNullOrWhiteSpace(request.System)) body["system"] = request.System;

        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var content = new JsonArray();
            foreach (var c in m.Content) content.Add(ContentToJson(c));
            messages.Add(new JsonObject { ["role"] = m.Role == AiRole.User ? "user" : "assistant", ["content"] = content });
        }
        body["messages"] = messages;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema.DeepClone(),
                });
            body["tools"] = tools;
        }
        return body;
    }

    private static JsonObject ContentToJson(AiContent c) => c switch
    {
        AiText t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        AiImage i => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = i.MediaType, ["data"] = Convert.ToBase64String(i.Data) },
        },
        AiToolUse u => new JsonObject { ["type"] = "tool_use", ["id"] = u.Id, ["name"] = u.Name, ["input"] = u.Input.DeepClone() },
        AiToolResult r => new JsonObject
        {
            ["type"] = "tool_result", ["tool_use_id"] = r.ToolUseId, ["content"] = r.Content, ["is_error"] = r.IsError,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static AiResponse ParseResponse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var content = new List<AiContent>();
        foreach (var node in root["content"]?.AsArray() ?? new JsonArray())
        {
            if (node is not JsonObject block) continue;
            switch (block["type"]?.GetValue<string>())
            {
                case "text":
                    content.Add(new AiText(block["text"]?.GetValue<string>() ?? ""));
                    break;
                case "tool_use":
                    content.Add(new AiToolUse(
                        block["id"]!.GetValue<string>(),
                        block["name"]!.GetValue<string>(),
                        block["input"] is JsonObject input ? (JsonObject)input.DeepClone() : new JsonObject()));
                    break;
                // Other block types (e.g. thinking) aren't used by the app and are skipped.
            }
        }
        var usage = root["usage"];
        return new AiResponse
        {
            Content = content,
            StopReason = root["stop_reason"]?.GetValue<string>() ?? "end_turn",
            Usage = new AiUsage(usage?["input_tokens"]?.GetValue<int>() ?? 0, usage?["output_tokens"]?.GetValue<int>() ?? 0),
        };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
