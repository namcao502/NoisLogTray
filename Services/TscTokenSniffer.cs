using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NoisLogTray;

// Playwright HEADLESS as a token source for GraphTscClient (port of
// lib/browser-tsc.ts). It never drives the Excel UI: it sniffs a
// Files.ReadWrite.All Graph Bearer off office.com, checks the saved session, and
// runs the one visible re-auth flow. Guarded by BrowserLock (one Chrome profile).
internal static class TscTokenSniffer
{
    internal static readonly string ProfileDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tsc-daily-log-browser");

    // Session check + token sniff run headless; only re-auth is visible.
    private const bool Headless = true;
    private const string GraphHost = "https://graph.microsoft.com/";

    // config/app.json reauthPollMs (270000): how long re-auth waits for the login.
    private const int ReauthPollTimeoutMs = 270_000;

    private static bool IsTscLoggedIn(string url) =>
        url.Contains("sharepoint.com") && !url.Contains("login") && !url.Contains("microsoftonline");

    private static bool HasAll(string? scp) =>
        Regex.IsMatch(scp ?? "", @"Files\.ReadWrite\.All|Sites\.ReadWrite\.All", RegexOptions.IgnoreCase);

    private static BrowserTypeLaunchPersistentContextOptions LaunchOptions(bool headless) => new()
    {
        Channel = "chrome",
        Headless = headless,
        ServiceWorkers = ServiceWorkerPolicy.Block,
        Args = new[] { "--disable-blink-features=AutomationControlled" },
        ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
    };

    // Reuse the authenticated TSC session as a token source: office.com mints a
    // broad delegated token including Files.ReadWrite.All / Sites.ReadWrite.All.
    // Collect every distinct graph token + scopes, then prefer the broadest.
    internal static async Task<(string Token, string Scopes)?> SniffGraphTokenAsync(Action<string>? onLog = null)
    {
        void Emit(string line) => onLog?.Invoke(line);

        if (!BrowserLock.TryAcquire())
        {
            Emit(BrowserLock.BusyMessage);
            return null;
        }

        try
        {
            using var pw = await Playwright.CreateAsync();
            await using var context = await pw.Chromium.LaunchPersistentContextAsync(ProfileDir, LaunchOptions(Headless));
            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

            var seen = new ConcurrentDictionary<string, string>();
            context.Request += (_, req) =>
            {
                if (!req.Url.StartsWith(GraphHost)) return;
                if (!req.Headers.TryGetValue("authorization", out var auth)) return;
                if (!auth.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) return;
                var token = auth[7..];
                seen.TryAdd(token, GraphTscClient.DecodeJwtScopes(token));
            };

            // office.com mints the broad (.All) token first; the OneDrive surfaces
            // only yield own-file scope, so they are fallback.
            var urls = new[]
            {
                "https://www.office.com/",
                "https://tscmiami0-my.sharepoint.com/_layouts/15/onedrive.aspx?view=7",
                "https://tscmiami0-my.sharepoint.com/_layouts/15/onedrive.aspx",
            };

            foreach (var url in urls)
            {
                Emit($"[graph-sniff] Loading {url}");
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                }
                catch (Exception e)
                {
                    Emit($"[graph-sniff] goto failed: {e.Message}");
                    continue;
                }
                var deadline = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < deadline && !seen.Values.Any(HasAll))
                    await page.WaitForTimeoutAsync(500);
                if (seen.Values.Any(HasAll)) break;
            }

            if (seen.IsEmpty)
            {
                Emit("[graph-sniff] No graph.microsoft.com Bearer seen. If you are logged out, run Check TSC and re-authenticate.");
                return null;
            }

            foreach (var scp in seen.Values.Distinct())
                Emit($"[graph-sniff] scopes seen: {(string.IsNullOrEmpty(scp) ? "(opaque/none)" : scp)}");

            var entries = seen.ToArray();
            var pick = entries.FirstOrDefault(kv => HasAll(kv.Value));
            if (pick.Key is null)
                pick = entries.FirstOrDefault(kv => Regex.IsMatch(kv.Value, @"Files\.ReadWrite|Sites\.ReadWrite", RegexOptions.IgnoreCase));
            if (pick.Key is null)
                pick = entries[0];

            var suffix = HasAll(pick.Value) ? "" : "  (no .All -> shared files will 403)";
            Emit($"[graph-sniff] Using token scopes: {(string.IsNullOrEmpty(pick.Value) ? "(opaque/none)" : pick.Value)}{suffix}");
            return (pick.Key, pick.Value);
        }
        catch (Exception e)
        {
            Emit($"[graph-sniff] Error: {e.Message}");
            return null;
        }
        finally
        {
            BrowserLock.Release();
        }
    }

    internal static async Task<(bool LoggedIn, string? Error)> CheckCredentialsAsync()
    {
        if (!BrowserLock.TryAcquire()) return (false, BrowserLock.BusyMessage);
        try
        {
            using var pw = await Playwright.CreateAsync();
            await using var context = await pw.Chromium.LaunchPersistentContextAsync(ProfileDir, LaunchOptions(Headless));
            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
            await page.GotoAsync(TscCells.ExcelUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await page.WaitForTimeoutAsync(2000);
            return (IsTscLoggedIn(page.Url), null);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
        finally
        {
            BrowserLock.Release();
        }
    }

    // The only visible browser flow: open Chrome so the user can complete a fresh
    // Microsoft login, then detect the session and persist it.
    internal static async Task<(bool Success, string? Error)> ReauthenticateAsync(Action<string>? onLog = null)
    {
        void Emit(string line) => onLog?.Invoke(line);

        if (!BrowserLock.TryAcquire())
        {
            Emit(BrowserLock.BusyMessage);
            return (false, BrowserLock.BusyMessage);
        }

        try
        {
            using var pw = await Playwright.CreateAsync();
            await using var context = await pw.Chromium.LaunchPersistentContextAsync(ProfileDir, LaunchOptions(false));
            Emit("[browser-tsc] Browser opened. Sign in to Microsoft in the window; this will detect your session.");

            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
            await page.GotoAsync(TscCells.ExcelUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

            var deadline = DateTime.UtcNow.AddMilliseconds(ReauthPollTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await page.WaitForTimeoutAsync(2000);
                string url;
                try
                {
                    url = page.Url;
                }
                catch
                {
                    Emit("[browser-tsc] Browser was closed before login completed.");
                    return (false, "Browser closed before login completed.");
                }
                if (IsTscLoggedIn(url))
                {
                    Emit("[browser-tsc] Login detected. Session saved.");
                    return (true, null);
                }
            }

            Emit("[browser-tsc] Timed out waiting for login.");
            return (false, "Timed out waiting for login.");
        }
        catch (Exception e)
        {
            Emit($"[browser-tsc] Error: {e.Message}");
            return (false, e.Message);
        }
        finally
        {
            BrowserLock.Release();
        }
    }
}
