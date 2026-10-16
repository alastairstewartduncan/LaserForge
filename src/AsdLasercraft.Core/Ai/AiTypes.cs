using System.Text.Json.Nodes;

namespace AsdLasercraft.Core.Ai;

/// <summary>One piece of a chat message: text, an image, a tool call from the model, or a tool result back to it.</summary>
public abstract record AiContent;

public sealed record AiText(string Text) : AiContent;

/// <summary>An image sent to the model. <paramref name="MediaType"/> is image/png, image/jpeg, image/webp or image/gif.</summary>
public sealed record AiImage(string MediaType, byte[] Data) : AiContent;

/// <summary>The model asking the app to run one of its tools.</summary>
public sealed record AiToolUse(string Id, string Name, JsonObject Input) : AiContent;

/// <summary>The app's answer to an <see cref="AiToolUse"/>.</summary>
public sealed record AiToolResult(string ToolUseId, string Content, bool IsError = false) : AiContent;

public enum AiRole { User, Assistant }

public sealed record AiMessage(AiRole Role, IReadOnlyList<AiContent> Content)
{
    public static AiMessage User(string text) => new(AiRole.User, new AiContent[] { new AiText(text) });
    public static AiMessage User(params AiContent[] content) => new(AiRole.User, content);
    public static AiMessage Assistant(string text) => new(AiRole.Assistant, new AiContent[] { new AiText(text) });

    /// <summary>All text blocks joined together.</summary>
    public string Text => string.Join("\n", Content.OfType<AiText>().Select(t => t.Text));
}

/// <summary>A tool the model may call. <see cref="InputSchema"/> is a JSON Schema object.</summary>
public sealed record AiTool(string Name, string Description, JsonObject InputSchema);

public sealed class AiRequest
{
    public string? System { get; init; }
    public List<AiMessage> Messages { get; init; } = new();
    public List<AiTool> Tools { get; init; } = new();
    public int MaxTokens { get; init; } = 2048;
    /// <summary>Overrides the client's default model for this request.</summary>
    public string? Model { get; init; }
    /// <summary>Short label used in logs and fakes, e.g. "design", "test-grid", "assistant".</summary>
    public string Purpose { get; init; } = "";
}

public sealed record AiUsage(int InputTokens, int OutputTokens);

public sealed class AiResponse
{
    public List<AiContent> Content { get; init; } = new();
    /// <summary>end_turn, tool_use, max_tokens, stop_sequence…</summary>
    public string StopReason { get; init; } = "end_turn";
    public AiUsage Usage { get; init; } = new(0, 0);

    public string Text => string.Join("\n", Content.OfType<AiText>().Select(t => t.Text));
    public IEnumerable<AiToolUse> ToolUses => Content.OfType<AiToolUse>();

    public static AiResponse FromText(string text) => new() { Content = { new AiText(text) } };
}

/// <summary>A problem talking to the AI service, phrased so it can be shown to the user as-is.</summary>
public sealed class AiException : Exception
{
    public AiErrorKind Kind { get; }
    public AiException(AiErrorKind kind, string message, Exception? inner = null) : base(message, inner) { Kind = kind; }
}

public enum AiErrorKind { NoKey, BadKey, RateLimited, Overloaded, Network, BadRequest, Server, BadResponse, Cancelled }

/// <summary>
/// Anything that can answer an <see cref="AiRequest"/>. The real implementation is <see cref="ClaudeClient"/>;
/// tests and the UI smoke test use <see cref="FakeAiClient"/> so they never need a key or a network.
/// </summary>
public interface IAiClient
{
    /// <summary>False when no API key has been set up (the UI then shows a "set up AI" prompt instead).</summary>
    bool IsConfigured { get; }
    Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A scripted client: each call is answered by the handler. Records every request it receives.</summary>
public sealed class FakeAiClient : IAiClient
{
    private readonly Func<AiRequest, AiResponse> _handler;
    public List<AiRequest> Requests { get; } = new();
    public bool IsConfigured { get; set; } = true;
    public TimeSpan Delay { get; set; }

    public FakeAiClient(Func<AiRequest, AiResponse> handler) { _handler = handler; }

    /// <summary>Answers calls in order with the given responses (the last one repeats).</summary>
    public static FakeAiClient Sequence(params AiResponse[] responses)
    {
        int i = 0;
        return new FakeAiClient(_ => responses[Math.Min(i++, responses.Length - 1)]);
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _handler(request);
    }
}
