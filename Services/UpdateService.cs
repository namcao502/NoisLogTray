using System.Text.Json;

namespace NoisLogTray;

// Checks GitHub Releases for a newer build. The repo is public, so the releases API is
// reachable anonymously (a modest unauthenticated rate limit is fine for a once-per-launch
// check). Any error or offline state returns null: an update check must never disrupt
// startup or surface an error to the user.
internal static class UpdateService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/namcao502/NoisLogTray/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub requires a User-Agent on every API request.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NoisLogTray-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    internal static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(LatestReleaseUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)) return null;
            if (!TryParseVersion(tag, out var latest)) return null;

            return latest > current ? new UpdateInfo(latest, url) : null;
        }
        catch
        {
            return null; // offline / rate-limited / unexpected shape -> no update, silently
        }
    }

    // Parse a release tag like "v1.2.3" or "1.2.3" into a Version; anything else -> false.
    private static bool TryParseVersion(string tag, out Version version)
    {
        if (Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }
}
