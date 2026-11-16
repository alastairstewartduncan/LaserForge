using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.App.Services;

/// <summary>
/// Per-user settings kept in %AppData%\ASD Lasercraft\settings.json: your machine profile (so every new project
/// starts with it), connection preferences and a few view options.
/// </summary>
public sealed class AppSettings
{
    public MachineProfile Machine { get; set; } = new();
    public string? LastPort { get; set; }
    public int LastBaud { get; set; } = 115200;
    public bool CheckForUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public bool ShowSplash { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    public bool ShowRulers { get; set; } = true;
    public double SimulatorSpeed { get; set; } = 4;
    public List<string> RecentFiles { get; set; } = new();

    // ---- AI (the API key itself lives in Windows Credential Manager, never in this file)
    /// <summary>Master switch: hides the AI menu and assistant when false.</summary>
    public bool AiEnabled { get; set; } = true;
    /// <summary>The Claude model chosen in versions before 0.5; now kept per provider in <see cref="AiProviders"/>.</summary>
    public string AiModel { get; set; } = Core.Ai.ClaudeClient.DefaultModel;
    /// <summary>AI services the app can use, each with its address, model and abilities. Keys are not stored here.</summary>
    public List<Core.Ai.AiProviderProfile> AiProviders { get; set; } = new();
    /// <summary>Id of the provider in use.</summary>
    public string AiProvider { get; set; } = Core.Ai.AiProviderCatalog.AnthropicId;
    /// <summary>The agent the assistant chat runs as ("" = the plain assistant).</summary>
    public string AssistantAgent { get; set; } = "";
    /// <summary>The designer and critic agents used when describing or refining a design.</summary>
    public string DesignerAgent { get; set; } = Core.Ai.Library.BuiltIns.DesignerName;
    public string CriticAgent { get; set; } = Core.Ai.Library.BuiltIns.CriticName;
    /// <summary>Extra skills ticked in the design window last time.</summary>
    public List<string> DesignSkills { get; set; } = new();
    /// <summary>Have the critic review and the designer revise before a design is shown.</summary>
    public bool DesignReview { get; set; } = true;
    public int DesignReviewRounds { get; set; } = 2;
    /// <summary>Lets the assistant change the design (every change is undoable). Off = advice only.</summary>
    public bool AssistantCanEdit { get; set; } = true;
    /// <summary>The user agreed that test-grid photos may be sent to Claude for analysis.</summary>
    public bool AiPhotoConsent { get; set; }
    /// <summary>The user agreed to the one-off background-removal model download.</summary>
    public bool BackgroundModelConsent { get; set; }
    /// <summary>Settings of the last material test grid, so the AI grid reader knows its layout.</summary>
    public Core.Generators.MaterialTestOptions? LastTestGrid { get; set; }

    [JsonIgnore] public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ASD Lasercraft");

    [JsonIgnore] public static string FilePath => Path.Combine(Folder, "settings.json");
    [JsonIgnore] public static string MaterialsPath => Path.Combine(Folder, "materials.json");
    /// <summary>Skills and agents live here as plain markdown files (skills\*.md, agents\*.md).</summary>
    [JsonIgnore] public static string AiLibraryFolder => Path.Combine(Folder, "ai");

    /// <summary>The provider in use (never null: falls back to the first one).</summary>
    [JsonIgnore] public Core.Ai.AiProviderProfile ActiveProvider
    {
        get
        {
            EnsureProviders();
            return AiProviders.FirstOrDefault(p => p.Id == AiProvider) ?? AiProviders[0];
        }
    }

    /// <summary>Fills in the built-in providers and carries the pre-0.5 Claude model choice across.</summary>
    public void EnsureProviders()
    {
        bool first = AiProviders.Count == 0;
        if (first || AiProviders.Count < Core.Ai.AiProviderCatalog.Defaults().Count)
            AiProviders = Core.Ai.AiProviderCatalog.Merge(AiProviders);
        if (first && !string.IsNullOrWhiteSpace(AiModel))
        {
            var claude = AiProviders.FirstOrDefault(p => p.Id == Core.Ai.AiProviderCatalog.AnthropicId);
            if (claude != null) claude.Model = AiModel.Trim();
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
                loaded.AiProviders ??= new();
                loaded.DesignSkills ??= new();
                loaded.EnsureProviders();
                return loaded;
            }
        }
        catch (Exception)
        {
            // Unreadable settings are not worth a crash: start fresh.
        }
        var fresh = new AppSettings();
        fresh.EnsureProviders();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Best effort: a read-only profile shouldn't stop the user working.
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 8) RecentFiles.RemoveRange(8, RecentFiles.Count - 8);
    }
}
