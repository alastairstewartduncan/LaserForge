using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AsdLasercraft.Core;

namespace AsdLasercraft.App.Services;

public sealed record UpdateInfo(string Version, string PageUrl, string? DownloadUrl, string Notes);

/// <summary>
/// Asks GitHub for the latest published release of this app and compares it with the running version.
/// Never downloads or replaces anything by itself: the user is shown the release page.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ASD-Lasercraft", AppInfo.Version));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>"owner/repo" taken from the repository URL in the build metadata.</summary>
    public static string? RepoSlug
    {
        get
        {
            if (!Uri.TryCreate(AppInfo.RepositoryUrl, UriKind.Absolute, out var uri)) return null;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
        }
    }

    /// <summary>Returns the newer release, or null when up to date, offline, or no release has been published yet.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var slug = RepoSlug;
        if (slug == null) return null;
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{slug}/releases/latest", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            string? download = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) || (download == null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                        download = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : download;
                }
            }
            var latest = tag.TrimStart('v', 'V');
            return IsNewer(latest, AppInfo.Version) ? new UpdateInfo(latest, page, download, notes) : null;
        }
        catch (Exception)
        {
            return null; // offline, rate-limited, blocked by a firewall… never bother the user about it
        }
    }

    /// <summary>Semantic-version comparison of "major.minor.patch" (pre-release suffixes ignored).</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static Version Parse(string s)
        {
            var core = s.Split('-', '+')[0];
            return Version.TryParse(core, out var v) ? v : new Version(0, 0, 0);
        }
        return Parse(candidate) > Parse(current);
    }
}
