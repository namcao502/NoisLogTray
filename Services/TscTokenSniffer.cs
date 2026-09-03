using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Win32;

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

    // Per-URL wait for a broad (.All) token. The M365 sign-in path redirects then does an
    // MSAL acquire before its first Graph call, which does not fit the old 12s.
    private const int SniffWaitSeconds = 30;

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

    internal const string ChromeMissingMessage =
        "Google Chrome is not installed. Install it from https://www.google.com/chrome, then retry " +
        "(the TSC sign-in uses your system Chrome).";

    // Chrome is launched via Channel = "chrome" (system Chrome, not Playwright's bundled
    // Chromium), so a clear "install Chrome" check up front beats a cryptic launch error.
    internal static bool ChromeInstalled() => FindChrome() != null;

    private static string? FindChrome()
    {
        // App Paths is the authoritative install pointer (covers non-default locations).
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }
        // Fallback to the common install locations.
        foreach (var p in new[]
                 {
                     @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                     @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
                     @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe",
                 })
        {
            var full = Environment.ExpandEnvironmentVariables(p);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // Turn a raw Playwright/browser launch failure into a plain, actionable line.
    private static string Explain(Exception e)
    {
        var m = e.Message;
        if (m.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            && (m.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || m.Contains("distribution", StringComparison.OrdinalIgnoreCase)))
            return ChromeMissingMessage;
        if (m.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            || m.Contains("playwright.ps1", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Driver not found", StringComparison.OrdinalIgnoreCase))
            return "Playwright could not start - its driver folder (.playwright) is missing next to the exe. " +
                   "Re-extract the full app zip. (Dev builds: run pwsh playwright.ps1 install chromium.)";
        return e.Message;
    }

    // Reuse the authenticated TSC session as a token source: office.com mints a
    // broad delegated token including Files.ReadWrite.All / Sites.ReadWrite.All.
    // Collect every distinct graph token + scopes, then prefer the broadest.
    internal static async Task<(string Token, string Scopes)?> SniffGraphTokenAsync(Action<string>? onLog = null)
    {
        void Emit(string line) => onLog?.Invoke(line);

        if (!ChromeInstalled())
        {
            Emit($"[graph-sniff] {ChromeMissingMessage}");
            return null;
        }

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

            // The M365 shell mints the broad (.All) token; OneDrive only yields own-file
            // scope, so it is fallback. /login is deliberate: the m365.cloud.microsoft root
            // is an anonymous marketing page that never auto-SSOs, so it mints nothing.
            var urls = new[]
            {
                "https://m365.cloud.microsoft/login?ru=%2F",
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
                var deadline = DateTime.UtcNow.AddSeconds(SniffWaitSeconds);
                while (DateTime.UtcNow < deadline && !seen.Values.Any(HasAll))
                    await page.WaitForTimeoutAsync(500);
                if (seen.Values.Any(HasAll)) break;
            }

            if (seen.IsEmpty)
            {
                Emit("[graph-sniff] No graph.microsoft.com Bearer seen - the saved TSC session is likely logged out. "
                     + "Use \"Re-authenticate TSC\" from the tray menu to sign in again; queued entries retry automatically.");
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

            var suffix = HasAll(pick.Value)
                ? ""
                : "  (no .All scope -> shared files will 403; use \"Re-authenticate TSC\" to refresh the session)";
            Emit($"[graph-sniff] Using token scopes: {(string.IsNullOrEmpty(pick.Value) ? "(opaque/none)" : pick.Value)}{suffix}");
            return (pick.Key, pick.Value);
        }
        catch (Exception e)
        {
            Emit($"[graph-sniff] {Explain(e)}");
            return null;
        }
        finally
        {
            BrowserLock.Release();
        }
    }

    internal static async Task<(bool LoggedIn, string? Error)> CheckCredentialsAsync()
    {
        if (!ChromeInstalled()) return (false, ChromeMissingMessage);
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
            return (false, Explain(e));
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

        if (!ChromeInstalled())
        {
            Emit($"[browser-tsc] {ChromeMissingMessage}");
            return (false, ChromeMissingMessage);
        }

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
            var msg = Explain(e);
            Emit($"[browser-tsc] {msg}");
            return (false, msg);
        }
        finally
        {
            BrowserLock.Release();
        }
    }
}
