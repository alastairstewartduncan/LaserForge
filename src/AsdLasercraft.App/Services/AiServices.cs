using System.Windows.Media;
using AsdLasercraft.Core.Ai;

namespace AsdLasercraft.App.Services;

/// <summary>
/// The app's single connection to Claude, plus shared AI helpers. The key comes from Windows Credential Manager on
/// every call, so changing it in AI settings takes effect immediately. The smoke test swaps in a fake client.
/// </summary>
public sealed class AiServices
{
    private readonly AppSettings _settings;
    private readonly ClaudeClient _claude;
    private static List<string>? _fonts;

    public AiServices(AppSettings settings)
    {
        _settings = settings;
        _claude = new ClaudeClient(() => SecretStore.ApiKey) { Model = settings.AiModel };
        Client = _claude;
    }

    /// <summary>The client every AI feature uses (a <see cref="FakeAiClient"/> in the smoke test).</summary>
    public IAiClient Client { get; internal set; }

    public bool Enabled => _settings.AiEnabled;
    public bool IsConfigured => Client.IsConfigured;

    /// <summary>Re-reads the model choice after AI settings change.</summary>
    public void Refresh() => _claude.Model = string.IsNullOrWhiteSpace(_settings.AiModel) ? ClaudeClient.DefaultModel : _settings.AiModel.Trim();

    public static IReadOnlyList<string> InstalledFonts =>
        _fonts ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(s => s).ToList();

    /// <summary>Checks the key and model with a tiny request. Returns null on success, else the problem.</summary>
    public async Task<string?> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await Client.SendAsync(new AiRequest
            {
                Purpose = "test",
                MaxTokens = 20,
                Messages = { AiMessage.User("Reply with just the word: ready") },
            }, ct);
            return r.Content.Count > 0 ? null : "The AI service answered, but with nothing.";
        }
        catch (AiException e)
        {
            return e.Message;
        }
    }
}
