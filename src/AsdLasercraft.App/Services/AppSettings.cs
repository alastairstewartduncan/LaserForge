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
    public string AiModel { get; set; } = Core.Ai.ClaudeClient.DefaultModel;
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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Unreadable settings are not worth a crash: start fresh.
        }
        return new AppSettings();
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
