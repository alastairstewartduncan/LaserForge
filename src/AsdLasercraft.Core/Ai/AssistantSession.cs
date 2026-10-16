using AsdLasercraft.Core.Grbl;

namespace AsdLasercraft.Core.Ai;

/// <summary>One assistant reply: its text plus the changes it made along the way.</summary>
public sealed class AssistantReply
{
    public string Text { get; init; } = "";
    public List<AssistantAction> Actions { get; init; } = new();
    public AiUsage Usage { get; init; } = new(0, 0);
}

/// <summary>
/// The assistant chat. Keeps the conversation, gives the model a fresh picture of the project on every turn, and
/// runs its tool calls (each one a normal undoable edit). Continuations stay on the caller's thread, so tools run on
/// the UI thread when called from the app.
/// </summary>
public sealed class AssistantSession
{
    public const int MaxToolRounds = 8;
    private const int MaxHistoryMessages = 30;

    private readonly IAiClient _client;
    private readonly IAssistantHost _host;
    private readonly AssistantTools _tools;
    private readonly List<AiMessage> _history = new();

    /// <summary>When false the assistant can only look and advise, never change the project.</summary>
    public bool CanEdit { get; set; } = true;

    public AssistantSession(IAiClient client, IAssistantHost host)
    {
        _client = client;
        _host = host;
        _tools = new AssistantTools(host);
    }

    public IReadOnlyList<AiMessage> History => _history;

    public void Reset() => _history.Clear();

    public async Task<AssistantReply> SendAsync(string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) throw new ArgumentException("Say something first.", nameof(userText));
        int rollback = _history.Count;
        _history.Add(AiMessage.User(userText.Trim()));
        var actions = new List<AssistantAction>();
        int inTokens = 0, outTokens = 0;
        try
        {
            for (int round = 0; ; round++)
            {
                var response = await _client.SendAsync(new AiRequest
                {
                    Purpose = "assistant",
                    System = BuildSystemPrompt(),
                    Messages = _history.ToList(),
                    Tools = AssistantTools.Definitions(CanEdit),
                    MaxTokens = 2000,
                }, ct);
                inTokens += response.Usage.InputTokens;
                outTokens += response.Usage.OutputTokens;
                _history.Add(new AiMessage(AiRole.Assistant, response.Content.ToList()));

                var calls = response.ToolUses.ToList();
                if (calls.Count == 0 || response.StopReason != "tool_use")
                {
                    Trim();
                    return new AssistantReply { Text = response.Text.Trim(), Actions = actions, Usage = new AiUsage(inTokens, outTokens) };
                }

                if (round >= MaxToolRounds)
                {
                    // Answer the pending calls so the history stays valid, then stop.
                    _history.Add(new AiMessage(AiRole.User, calls.Select(c => (AiContent)new AiToolResult(c.Id, "Stopped: too many steps in one turn.", true)).ToList()));
                    Trim();
                    return new AssistantReply
                    {
                        Text = (response.Text.Trim() + "\n\n(I stopped after several steps – tell me if you'd like me to carry on.)").Trim(),
                        Actions = actions,
                        Usage = new AiUsage(inTokens, outTokens),
                    };
                }

                var results = new List<AiContent>();
                foreach (var call in calls)
                {
                    var (result, action) = await _tools.ExecuteAsync(call, CanEdit, ct);
                    results.Add(result);
                    if (action != null) actions.Add(action);
                }
                _history.Add(new AiMessage(AiRole.User, results));
            }
        }
        catch
        {
            // Leave the conversation as it was before this message so the user can simply try again.
            _history.RemoveRange(rollback, _history.Count - rollback);
            throw;
        }
    }

    /// <summary>Drops the oldest exchanges, always cutting at a plain user message so tool calls stay paired.</summary>
    private void Trim()
    {
        if (_history.Count <= MaxHistoryMessages) return;
        int cut = _history.Count - MaxHistoryMessages;
        while (cut < _history.Count && !(_history[cut].Role == AiRole.User && _history[cut].Content.All(c => c is AiText))) cut++;
        if (cut < _history.Count) _history.RemoveRange(0, cut);
    }

    public string BuildSystemPrompt()
    {
        var console = _host.RecentConsole.Count == 0 ? "(nothing yet)" : string.Join("\n", _host.RecentConsole.TakeLast(15));
        return $"""
            You are the assistant built into A·S·D Lasercraft, a Windows app for designing and burning with GRBL diode lasers.
            Be friendly, practical and brief (a few sentences; short lists when useful). Use UK English and millimetres.

            What you can do: answer questions about laser settings, materials, the app and GRBL messages; and{(CanEdit ? "" : " (editing is OFF – only advise)")} change the
            user's design with your tools: layers and their settings, material presets, text, shapes, moving/resizing, arrays, and new AI artwork.
            What you can't do: you cannot frame, start, pause or stop a burn, connect to the laser, or send G-code. Never say you have.
            When the user wants to burn, tell them to use Frame then Start on the Laser tab (or Preview to simulate it first).

            Working rules:
            - Coordinates are mm from the bed's top-left corner, X right, Y down. The design should stay inside the bed.
            - Layers burn top to bottom; cuts should be last. Line = outlines (cut/score), Fill = engrave inside closed shapes, Image = bitmaps.
            - Prefer the user's saved material presets. When unsure about a new material, suggest Tools → Material test grid, then AI → Read a test grid photo.
            - After edits, briefly say what changed. Everything you do can be undone with Ctrl+Z.
            - Ask a short question if a request is ambiguous rather than guessing wildly.

            Safety (mention when relevant, don't lecture): never laser PVC, vinyl, faux leather or anything containing chlorine (toxic, corrosive gas),
            or polycarbonate/ABS; ventilate; wear glasses rated for the laser's wavelength; never leave a burn unattended; keep a fire extinguisher handy.

            Current project:
            {_tools.DescribeProject()}
            Laser console (most recent last):
            {console}

            GRBL codes, as the app explains them:
            {GrblCodes.ReferenceText()}
            """;
    }
}
