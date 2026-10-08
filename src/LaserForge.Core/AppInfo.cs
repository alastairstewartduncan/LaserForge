using System.Reflection;

namespace LaserForge.Core;

/// <summary>
/// Product, version and author details, read from the assembly attributes set in Directory.Build.props.
/// Used by the About box, the window title and the G-code header so they never drift apart.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Asm = typeof(AppInfo).Assembly;

    public static string Product => Asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "LaserForge";

    /// <summary>Full informational version, e.g. "0.1.1+a1b2c3d4…" on CI builds.</summary>
    public static string InformationalVersion =>
        Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Semantic version without build metadata, e.g. "0.1.1".</summary>
    public static string Version => InformationalVersion.Split('+')[0];

    /// <summary>Short commit hash if the build recorded one, otherwise null.</summary>
    public static string? Commit
    {
        get
        {
            var parts = InformationalVersion.Split('+');
            return parts.Length > 1 && parts[1].Length > 0 ? parts[1][..Math.Min(7, parts[1].Length)] : null;
        }
    }

    public static string BuildNumber => Metadata("BuildNumber") ?? "0";
    public static string Author => Asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";
    public static string Copyright => Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
    public static string Description => Asm.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "";
    public static string AuthorEmail => Metadata("AuthorEmail") ?? "";
    public static string RepositoryUrl => Metadata("RepositoryUrl") ?? "";

    /// <summary>One-line version string, e.g. "0.1.1 (build 12, a1b2c3d)".</summary>
    public static string VersionDisplay
    {
        get
        {
            var extra = new List<string>();
            if (BuildNumber != "0") extra.Add($"build {BuildNumber}");
            if (Commit != null) extra.Add(Commit);
            return extra.Count == 0 ? Version : $"{Version} ({string.Join(", ", extra)})";
        }
    }

    private static string? Metadata(string key) =>
        Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
