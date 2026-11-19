using System.IO;
using System.Windows.Media;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Ai.Library;

namespace AsdLasercraft.App.Services;

/// <summary>
/// The app's connection to whichever AI provider is chosen, the skills and agents library, and shared AI helpers.
/// Keys come from Windows Credential Manager on every call, so changes in AI settings take effect immediately.
/// The smoke test swaps in a fake client.
/// </summary>
public sealed class AiServices
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, IAiClient> _clients = new();
    private static List<string>? _fonts;
    private bool _fake;

    public AiServices(AppSettings settings, string? libraryFolder = null)
    {
        _settings = settings;
        _settings.EnsureProviders();
        LibraryFolder = libraryFolder ?? AppSettings.AiLibraryFolder;
        Library = AiLibrary.Load(LibraryFolder);
        Client = ClientForProvider(_settings.ActiveProvider);
    }

    public AppSettings Settings => _settings;
    public string LibraryFolder { get; }

    /// <summary>The user's skills and agents.</summary>
    public AiLibrary Library { get; private set; }

    /// <summary>The client for the active provider (a <see cref="FakeAiClient"/> in the smoke test).</summary>
    public IAiClient Client { get; private set; }

    public AiProviderProfile Active => _settings.ActiveProvider;
    public bool Enabled => _settings.AiEnabled;
    public bool IsConfigured => _fake || (Client.IsConfigured && AiClientFactory.IsReady(Active, SecretStore.GetKey(Active.Id)));
    public bool IsFake => _fake;

    /// <summary>"OpenAI · model-name", for status lines.</summary>
    public string StatusLabel => _fake ? "Demo" : Active.Label;

    /// <summary>Short provider name for sentences: "Claude", "OpenAI", "Ollama".</summary>
    public string ProviderName => _fake ? "the AI" : Active.Kind == AiProviderKind.Anthropic ? "Claude" : AiClientFactory.ShortName(Active);

    /// <summary>Rebuilds the clients after AI settings change.</summary>
    public void Refresh()
    {
        if (_fake) return;
        foreach (var c in _clients.Values) (c as IDisposable)?.Dispose();
        _clients.Clear();
        _settings.EnsureProviders();
        Client = ClientForProvider(Active);
    }

    private IAiClient ClientForProvider(AiProviderProfile p)
    {
        if (!_clients.TryGetValue(p.Id, out var client))
        {
            var id = p.Id;
            client = AiClientFactory.Create(p, () => SecretStore.GetKey(id));
            _clients[p.Id] = client;
        }
        return client;
    }

    /// <summary>The provider an agent will really use: its own if it names one that is set up, otherwise the active one.</summary>
    public AiProviderProfile ProviderFor(Agent? agent)
    {
        if (_fake || agent == null || string.IsNullOrWhiteSpace(agent.Provider)) return Active;
        var p = _settings.AiProviders.FirstOrDefault(x => x.Id == agent.Provider);
        if (p == null) return Active;
        // An agent's own model can stand in for the provider's, so judge readiness with it applied.
        var probe = p.Clone();
        if (!string.IsNullOrWhiteSpace(agent.Model)) probe.Model = agent.Model;
        return AiClientFactory.IsReady(probe, SecretStore.GetKey(p.Id)) ? p : Active;
    }

    public IAiClient ClientFor(Agent? agent) => _fake ? Client : ClientForProvider(ProviderFor(agent));

    /// <summary>
    /// The model an agent asks for, or null to use the provider's own. Ignored when the agent's provider isn't
    /// available, because a model name from one provider means nothing to another.
    /// </summary>
    public string? ModelFor(Agent? agent)
    {
        if (_fake || agent == null || string.IsNullOrWhiteSpace(agent.Model)) return null;
        if (!string.IsNullOrWhiteSpace(agent.Provider) && ProviderFor(agent).Id != agent.Provider) return null;
        return agent.Model.Trim();
    }

    public bool SupportsVision(Agent? agent = null) => _fake || ProviderFor(agent).SupportsVision;
    public bool SupportsTools(Agent? agent = null) => _fake || ProviderFor(agent).SupportsTools;

    // ---- library

    public void SaveLibrary()
    {
        if (_fake) return; // the smoke test must not write to the user's profile
        try { Library.Save(LibraryFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort, like settings */ }
    }

    public Agent? AssistantAgent => Library.FindAgent(_settings.AssistantAgent);

    public Agent? DesignerAgent =>
        Library.FindAgent(_settings.DesignerAgent) is { Role: AgentRole.Designer } a ? a : Library.AgentsFor(AgentRole.Designer).FirstOrDefault();

    public Agent? CriticAgent =>
        Library.FindAgent(_settings.CriticAgent) is { Role: AgentRole.Critic } a ? a : Library.AgentsFor(AgentRole.Critic).FirstOrDefault();

    /// <summary>A design generator set up for a designer agent and any extra skills.</summary>
    public DesignGenerator NewDesigner(Agent? designer, IEnumerable<Skill>? extraSkills = null) =>
        new(ClientFor(designer), WpfTextOutliner.Instance)
        {
            ExtraInstructions = AgentRuntime.Compose(designer, Library, extraSkills),
            Model = ModelFor(designer),
        };

    /// <summary>Review settings for a critic agent (null critic = hard checks only).</summary>
    public RefineOptions NewRefineOptions(Agent? critic, DesignStyle style, double bedWidthMm, double bedHeightMm, IEnumerable<Skill>? extraSkills = null) => new()
    {
        Style = style,
        MaxRevisions = Math.Clamp(_settings.DesignReviewRounds, 1, 4),
        BedWidthMm = bedWidthMm,
        BedHeightMm = bedHeightMm,
        CriticClient = critic == null ? null : ClientFor(critic),
        CriticInstructions = critic == null ? null : AgentRuntime.Compose(critic, Library, extraSkills),
        CriticModel = ModelFor(critic),
    };

    public static IReadOnlyList<string> InstalledFonts =>
        _fonts ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(s => s).ToList();

    /// <summary>Checks a provider profile and key with a tiny request. Returns null on success, else the problem.</summary>
    public static async Task<string?> TestAsync(AiProviderProfile profile, string? key, CancellationToken ct = default)
    {
        var client = AiClientFactory.Create(profile, () => key);
        try
        {
            await client.SendAsync(new AiRequest { Purpose = "test", MaxTokens = 64, Messages = { AiMessage.User("Reply with just the word: ready") } }, ct);
            return null;
        }
        catch (AiException e)
        {
            return e.Message;
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    // ---- smoke test

    internal void UseFake(IAiClient client)
    {
        _fake = true;
        Client = client;
        var lib = new AiLibrary();
        lib.AddBuiltIns();
        Library = lib;
    }
}
