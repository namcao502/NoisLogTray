using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

// Sniff smoke test: prove Playwright for .NET can launch the system Chrome
// (channel=chrome) against the existing ~/.tsc-daily-log-browser profile and
// sniff a Microsoft Graph Bearer token off office.com. Mirrors the mechanism in
// lib/browser-tsc.ts sniffGraphToken. No writes; read-only verification.

const string GraphHost = "https://graph.microsoft.com/";
var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tsc-daily-log-browser");
Console.WriteLine($"Profile dir: {profileDir} (exists={Directory.Exists(profileDir)})");

static string DecodeScopes(string token)
{
    try
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return "";
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = (payload.Length % 4) switch { 2 => payload + "==", 3 => payload + "=", _ => payload };
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("scp", out var scp) && scp.ValueKind == JsonValueKind.String)
            return scp.GetString() ?? "";
        return "";
    }
    catch { return ""; }
}

bool HasAll(string scp) => Regex.IsMatch(scp, @"Files\.ReadWrite\.All|Sites\.ReadWrite\.All", RegexOptions.IgnoreCase);

var seen = new ConcurrentDictionary<string, string>();

try
{
    using var pw = await Playwright.CreateAsync();
    Console.WriteLine("Playwright driver ready. Launching Chrome (channel=chrome, headless)...");

    await using var context = await pw.Chromium.LaunchPersistentContextAsync(profileDir, new BrowserTypeLaunchPersistentContextOptions
    {
        Channel = "chrome",
        Headless = true,
        ServiceWorkers = ServiceWorkerPolicy.Block,
        Args = new[] { "--disable-blink-features=AutomationControlled" },
        ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
    });
    Console.WriteLine("Chrome launched against the saved profile. OK.");

    context.Request += (_, req) =>
    {
        if (!req.Url.StartsWith(GraphHost)) return;
        if (!req.Headers.TryGetValue("authorization", out var auth)) return;
        if (!auth.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) return;
        var tok = auth[7..];
        seen.TryAdd(tok, DecodeScopes(tok));
    };

    var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
    var urls = new[]
    {
        "https://www.office.com/",
        "https://tscmiami0-my.sharepoint.com/_layouts/15/onedrive.aspx?view=7",
    };

    foreach (var url in urls)
    {
        Console.WriteLine($"Loading {url}");
        try { await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }); }
        catch (Exception e) { Console.WriteLine($"  goto failed: {e.Message}"); continue; }

        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline && !seen.Values.Any(HasAll))
            await page.WaitForTimeoutAsync(500);
        if (seen.Values.Any(HasAll)) break;
    }

    Console.WriteLine($"Distinct graph tokens sniffed: {seen.Count}");
    foreach (var scp in seen.Values.Distinct())
        Console.WriteLine($"  scopes: {(string.IsNullOrEmpty(scp) ? "(opaque/none)" : scp)}");

    if (seen.Values.Any(HasAll))
    {
        Console.WriteLine("\nSNIFF PASSED: Playwright launched Chrome against the profile AND sniffed a Files.ReadWrite.All / Sites.ReadWrite.All token.");
        return 0;
    }
    if (seen.Count > 0)
    {
        Console.WriteLine("\nSNIFF MECHANISM OK (partial): tokens sniffed but none with .All. Session may lack shared-file scope; production would fall through to re-auth.");
        return 0;
    }
    Console.WriteLine("\nSNIFF MECHANISM OK, NO TOKEN: Chrome launched fine but no Graph Bearer seen -- the saved TSC session is likely logged out (production shows 'Check TSC / re-authenticate').");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    if (ex.Message.Contains("Executable doesn't exist") || ex.Message.Contains("playwright.ps1") || ex.Message.Contains("install"))
        Console.WriteLine("  -> driver/browser not installed. Run: pwsh <bin>/playwright.ps1 install chromium");
    return 3;
}
