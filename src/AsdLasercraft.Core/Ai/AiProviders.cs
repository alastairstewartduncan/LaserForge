using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace AsdLasercraft.Core.Ai;

/// <summary>Which wire format a provider speaks.</summary>
public enum AiProviderKind
{
    /// <summary>Anthropic's Claude Messages API.</summary>
    Anthropic,
    /// <summary>OpenAI Chat Completions format: OpenAI, OpenRouter, Ollama, LM Studio and many others.</summary>
    OpenAiCompatible,
    /// <summary>Google's Gemini API.</summary>
    Gemini,
}

/// <summary>
/// One AI service the app can use: where it lives, which model to ask for and what that model can do.
/// Saved in settings; the API key is kept separately in Windows Credential Manager under <see cref="Id"/>.
/// </summary>
public sealed class AiProviderProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AiProviderKind Kind { get; set; } = AiProviderKind.OpenAiCompatible;
    /// <summary>API address. Empty = the provider's standard address.</summary>
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>False for local servers that need no key.</summary>
    public bool KeyRequired { get; set; } = true;
    /// <summary>The chosen model can look at pictures (needed to read test-grid photos).</summary>
    public bool SupportsVision { get; set; } = true;
    /// <summary>The chosen model can call tools (needed for the assistant to edit the design).</summary>
    public bool SupportsTools { get; set; } = true;
    public bool UseMaxCompletionTokens { get; set; }
    /// <summary>Where the user gets a key (shown as a link in AI settings).</summary>
    public string KeyUrl { get; set; } = "";

    public AiProviderProfile Clone() => (AiProviderProfile)MemberwiseClone();

    /// <summary>A short label for status lines: "OpenAI · gpt-x".</summary>
    public string Label => string.IsNullOrWhiteSpace(Model) ? Name : $"{Name} · {Model}";

    public override string ToString() => Name;
}

/// <summary>The providers offered out of the box. Users can change the address and model of any of them.</summary>
public static class AiProviderCatalog
{
    public const string AnthropicId = "anthropic";
    public const string OpenAiId = "openai";
    public const string GeminiId = "gemini";
    public const string OpenRouterId = "openrouter";
    public const string OllamaId = "ollama";
    public const string LmStudioId = "lmstudio";
    public const string CustomId = "custom";

    public static List<AiProviderProfile> Defaults() => new()
    {
        new() { Id = AnthropicId, Name = "Anthropic Claude", Kind = AiProviderKind.Anthropic, Model = ClaudeClient.DefaultModel,
                KeyUrl = "https://console.anthropic.com/settings/keys" },
        new() { Id = OpenAiId, Name = "OpenAI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://api.openai.com/v1",
                UseMaxCompletionTokens = true, KeyUrl = "https://platform.openai.com/api-keys" },
        new() { Id = GeminiId, Name = "Google Gemini", Kind = AiProviderKind.Gemini, KeyUrl = "https://aistudio.google.com/apikey" },
        new() { Id = OpenRouterId, Name = "OpenRouter", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://openrouter.ai/api/v1",
                KeyUrl = "https://openrouter.ai/keys" },
        new() { Id = OllamaId, Name = "Ollama (on this PC)", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:11434/v1",
                KeyRequired = false, SupportsVision = false },
        new() { Id = LmStudioId, Name = "LM Studio (on this PC)", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:1234/v1",
                KeyRequired = false, SupportsVision = false },
        new() { Id = CustomId, Name = "Custom (OpenAI-compatible)", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "", KeyRequired = false },
    };

    /// <summary>
    /// Adds any built-in providers missing from a saved list (so new ones appear after an update) and returns it.
    /// Saved entries keep the user's address, model and capability choices.
    /// </summary>
    public static List<AiProviderProfile> Merge(List<AiProviderProfile>? saved)
    {
        var result = (saved ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Id)).GroupBy(p => p.Id).Select(g => g.First()).ToList();
        var defaults = Defaults();
        foreach (var d in defaults)
        {
            var existing = result.FirstOrDefault(p => p.Id == d.Id);
            if (existing == null) result.Add(d);
            else
            {
                existing.Kind = d.Kind; // the wire format of a built-in never changes
                if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = d.Name;
                if (string.IsNullOrWhiteSpace(existing.KeyUrl)) existing.KeyUrl = d.KeyUrl;
            }
        }
        // Built-ins first, in catalogue order, then anything else.
        return result.OrderBy(p => { int i = defaults.FindIndex(d => d.Id == p.Id); return i < 0 ? int.MaxValue : i; }).ToList();
    }

    /// <summary>Model IDs worth offering before the live list has been fetched. Deliberately short: model names change often.</summary>
    public static IReadOnlyList<string> SuggestedModels(AiProviderProfile p) => p.Kind switch
    {
        AiProviderKind.Anthropic => ClaudeClient.SuggestedModels,
        AiProviderKind.Gemini => new[] { "gemini-flash-latest", "gemini-pro-latest" },
        _ => Array.Empty<string>(),
    };
}

/// <summary>Builds the right client for a provider profile.</summary>
public static class AiClientFactory
{
    public static IAiClient Create(AiProviderProfile p, Func<string?> apiKey, HttpClient? http = null, string? modelOverride = null)
    {
        var model = string.IsNullOrWhiteSpace(modelOverride) ? p.Model.Trim() : modelOverride.Trim();
        switch (p.Kind)
        {
            case AiProviderKind.Anthropic:
            {
                var c = new ClaudeClient(apiKey, http) { Model = model.Length > 0 ? model : ClaudeClient.DefaultModel };
                if (!string.IsNullOrWhiteSpace(p.BaseUrl) && Uri.TryCreate(p.BaseUrl.Trim().TrimEnd('/') + "/v1/messages", UriKind.Absolute, out var uri))
                    c.EndpointUri = uri;
                return c;
            }
            case AiProviderKind.Gemini:
                return new GeminiClient(apiKey, http) { Model = model, BaseUrl = string.IsNullOrWhiteSpace(p.BaseUrl) ? GeminiClient.DefaultBaseUrl : p.BaseUrl };
            default:
                return new OpenAiCompatibleClient(apiKey, p.BaseUrl, http)
                {
                    Model = model, ProviderName = ShortName(p), KeyRequired = p.KeyRequired, UseMaxCompletionTokens = p.UseMaxCompletionTokens,
                };
        }
    }

    public static string ShortName(AiProviderProfile p)
    {
        int i = p.Name.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? p.Name[..i] : p.Name.Length > 0 ? p.Name : "AI";
    }

    /// <summary>True when the profile has what it needs to make a call (key if required, address, model).</summary>
    public static bool IsReady(AiProviderProfile p, string? key) =>
        (!p.KeyRequired || !string.IsNullOrWhiteSpace(key)) &&
        (p.Kind != AiProviderKind.OpenAiCompatible || !string.IsNullOrWhiteSpace(p.BaseUrl)) &&
        (p.Kind == AiProviderKind.Anthropic || !string.IsNullOrWhiteSpace(p.Model));
}

/// <summary>Asks a provider which models the key can use, so the user picks from a live list instead of typing IDs.</summary>
public static class AiModelLister
{
    public static async Task<List<string>> ListAsync(AiProviderProfile p, string? key, HttpClient? http = null, CancellationToken ct = default)
    {
        bool owns = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, ListUri(p));
            key = key?.Trim();
            switch (p.Kind)
            {
                case AiProviderKind.Anthropic:
                    if (string.IsNullOrEmpty(key)) throw NoKey(p);
                    msg.Headers.Add("x-api-key", key);
                    msg.Headers.Add("anthropic-version", ClaudeClient.ApiVersion);
                    break;
                case AiProviderKind.Gemini:
                    if (string.IsNullOrEmpty(key)) throw NoKey(p);
                    msg.Headers.Add("x-goog-api-key", key);
                    break;
                default:
                    if (p.KeyRequired && string.IsNullOrEmpty(key)) throw NoKey(p);
                    if (!string.IsNullOrEmpty(key)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    break;
            }
            var (status, text) = await AiHttp.SendAsync(http, msg, ct).ConfigureAwait(false);
            if ((int)status is < 200 or >= 300) throw ClaudeClient.ErrorFor(status, text, AiClientFactory.ShortName(p));
            try { return Parse(p.Kind, text); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                throw new AiException(AiErrorKind.BadResponse, "The model list couldn't be read. You can still type a model name.", ex);
            }
        }
        finally
        {
            if (owns) http.Dispose();
        }

        static AiException NoKey(AiProviderProfile p) => new(AiErrorKind.NoKey, $"Enter the {AiClientFactory.ShortName(p)} API key first.");
    }

    public static Uri ListUri(AiProviderProfile p)
    {
        try
        {
            return p.Kind switch
            {
                AiProviderKind.Anthropic => new Uri((string.IsNullOrWhiteSpace(p.BaseUrl) ? "https://api.anthropic.com" : p.BaseUrl.Trim().TrimEnd('/')) + "/v1/models?limit=100"),
                AiProviderKind.Gemini => new Uri((string.IsNullOrWhiteSpace(p.BaseUrl) ? GeminiClient.DefaultBaseUrl : p.BaseUrl.Trim().TrimEnd('/')) + "/models?pageSize=200"),
                _ => OpenAiCompatibleClient.EndpointFor(p.BaseUrl, "models"),
            };
        }
        catch (UriFormatException)
        {
            throw new AiException(AiErrorKind.BadRequest, "That address isn't a valid URL.");
        }
    }

    public static List<string> Parse(AiProviderKind kind, string json)
    {
        var root = JsonNode.Parse(json);
        var ids = new List<string>();
        if (kind == AiProviderKind.Gemini)
        {
            foreach (var m in (root?["models"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var methods = m["supportedGenerationMethods"] as JsonArray;
                if (methods != null && !methods.Any(x => x?.ToString() == "generateContent")) continue;
                var name = m["name"]?.ToString() ?? "";
                if (name.StartsWith("models/")) name = name[7..];
                if (name.Length > 0) ids.Add(name);
            }
        }
        else
        {
            var data = root is JsonArray a ? a : root?["data"] as JsonArray ?? root?["models"] as JsonArray ?? new JsonArray();
            foreach (var m in data.OfType<JsonObject>())
            {
                var id = m["id"]?.ToString() ?? m["name"]?.ToString() ?? "";
                if (id.Length > 0) ids.Add(id);
            }
        }
        return ids.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
