using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;

// Graph auth spike (Option A). Question it answers: can a PUBLIC, pre-consented
// first-party client id get a delegated Files.ReadWrite.All token in THIS tenant via
// device code - with no custom app registration and no admin consent? If yes, the app
// can retire the Playwright/Chrome token sniff for a clean MSAL flow (silent refresh,
// no browser automation). Read-only: after acquiring a token it lists the TSC workbook's
// worksheets to prove the token really authorizes that file (a read with a ReadWrite
// scope is strong evidence write will work; the spike never writes to the shared file).
//
// STATUS (tested 2026-08, nam.nguyen@tscmiami.com): BLOCKED - the tenant disables user
// consent, so the Graph CLI client returns "Need admin approval" for Files.ReadWrite.All.
// The scope/client/flow are all fine; the only blocker is tenant consent policy. The
// Playwright sniff still works because it borrows Microsoft's already-consented first-party
// apps. So the app stays on the sniff for now.
//
// KEPT for the future: if an admin ever prepares our account - i.e. registers a dedicated
// public-client app (allow public client flows), delegated Files.ReadWrite.All, admin
// consent, NO secret - point this spike at that client + tenant id to confirm, then
// migrate to MSAL device code (primary) with the sniff as fallback. Until then: parked.

// Public clients to try, in order. Microsoft Graph Command Line Tools is preauthorized
// by Microsoft to call Microsoft Graph with delegated scopes via device code - the right
// borrowed client here. (Azure CLI, 04b07795-..., is preauthorized for Azure Resource
// Manager, NOT arbitrary Graph scopes, so it returns AADSTS65002 for Files.ReadWrite.All -
// that is a wrong-client error, not a tenant policy block. Dropped for a clean signal.)
var clients = new (string Name, string ClientId)[]
{
    ("Microsoft Graph Command Line Tools", "14d82eec-204b-4c2f-b7e8-296a70dab67e"),
};

// Delegated scope the TSC write needs. Sites.ReadWrite.All is an acceptable alternative.
var scopes = new[] { "https://graph.microsoft.com/Files.ReadWrite.All" };

// "organizations" = work/school accounts (not personal Microsoft accounts). If a tenant
// blocks that, swap for the explicit tenant id: https://login.microsoftonline.com/<tenant>.
const string authority = "https://login.microsoftonline.com/organizations";

// The shared TSC workbook (default from TscCells.ExcelUrl; override here if yours differs).
const string shareUrl =
    "https://tscmiami0-my.sharepoint.com/:x:/r/personal/dave_markert_tscmiami_com/_layouts/15/doc2.aspx?sourcedoc=%7B1AE62FA5-2E6F-47E6-B6B6-BFF724E1A08C%7D&file=TSC%20Development%20WIP.xlsx&action=default&mobileredirect=true";

foreach (var (name, clientId) in clients)
{
    Console.WriteLine($"\n=== Trying public client: {name} ({clientId}) ===");
    try
    {
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(authority)
            .Build();

        var result = await app.AcquireTokenWithDeviceCode(scopes, dc =>
        {
            Console.WriteLine();
            Console.WriteLine(dc.Message); // "go to https://microsoft.com/devicelogin and enter CODE"
            Console.WriteLine();
            return Task.CompletedTask;
        }).ExecuteAsync();

        Console.WriteLine($"TOKEN ACQUIRED for {result.Account?.Username}");
        Console.WriteLine($"Granted scopes (as reported by AAD): {string.Join(" ", result.Scopes)}");

        var scp = DecodeScopes(result.AccessToken);
        Console.WriteLine($"Token scp claim: {(string.IsNullOrEmpty(scp) ? "(opaque/none)" : scp)}");
        var hasWrite = Regex.IsMatch(scp, @"Files\.ReadWrite(\.All)?|Sites\.ReadWrite\.All", RegexOptions.IgnoreCase);
        Console.WriteLine(hasWrite
            ? "  -> token HAS a file write scope."
            : "  -> WARNING: token has no Files/Sites ReadWrite scope; write would fail.");

        Console.WriteLine("\nProbing the TSC workbook (read-only) ...");
        var readOk = await TryListWorksheetsAsync(result.AccessToken, shareUrl);

        Console.WriteLine();
        if (hasWrite && readOk)
        {
            Console.WriteLine($"SPIKE RESULT: SUCCESS with '{name}'. Option A is viable in this tenant:");
            Console.WriteLine("  MSAL device code -> write-scoped token -> real workbook access, no admin, no Chrome.");
            return 0;
        }
        if (hasWrite)
        {
            Console.WriteLine($"SPIKE RESULT: PARTIAL with '{name}'. Got a write-scoped token, but the workbook probe failed");
            Console.WriteLine("  (likely the share URL / permissions, not auth). Auth path is promising; check the read error above.");
            return 0;
        }
        Console.WriteLine($"SPIKE RESULT: PARTIAL with '{name}'. Signed in, but no write scope was granted for this client.");
        return 0;
    }
    catch (MsalException ex)
    {
        Console.WriteLine($"  {name} failed: {ex.ErrorCode}: {ex.Message}");
        // Try the next client id.
    }
}

Console.WriteLine("\nSPIKE RESULT: BLOCKED. No public client could obtain a Files scope in this tenant.");
Console.WriteLine("  Option A is not viable without help. Next: path B (ask an admin to register + consent an app),");
Console.WriteLine("  or keep the Playwright sniff (path C).");
return 1;

// Pull the space-delimited scopes out of a Graph JWT's payload (scp claim).
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
        return doc.RootElement.TryGetProperty("scp", out var scp) && scp.ValueKind == JsonValueKind.String
            ? scp.GetString() ?? ""
            : "";
    }
    catch { return ""; }
}

// Resolve the shared workbook and list its worksheet names - a non-destructive read
// that proves the token authorizes real access to the file.
static async Task<bool> TryListWorksheetsAsync(string accessToken, string url)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var resolve = await http.GetAsync($"https://graph.microsoft.com/v1.0/shares/{EncodeShareId(url)}/driveItem");
        if (!resolve.IsSuccessStatusCode)
        {
            Console.WriteLine($"  shares/driveItem failed: {(int)resolve.StatusCode} {resolve.ReasonPhrase}");
            return false;
        }
        using var doc = JsonDocument.Parse(await resolve.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var itemId = root.GetProperty("id").GetString();
        var driveId = root.GetProperty("parentReference").GetProperty("driveId").GetString();

        var sheetsRes = await http.GetAsync(
            $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{itemId}/workbook/worksheets");
        if (!sheetsRes.IsSuccessStatusCode)
        {
            Console.WriteLine($"  workbook/worksheets failed: {(int)sheetsRes.StatusCode} {sheetsRes.ReasonPhrase}");
            return false;
        }
        using var sheets = JsonDocument.Parse(await sheetsRes.Content.ReadAsStringAsync());
        var names = sheets.RootElement.GetProperty("value").EnumerateArray()
            .Select(w => w.GetProperty("name").GetString())
            .ToArray();
        Console.WriteLine($"  workbook OK. Worksheets: {string.Join(", ", names)}");
        return true;
    }
    catch (Exception e)
    {
        Console.WriteLine($"  workbook probe error: {e.Message}");
        return false;
    }
}

// Graph share-id encoding: "u!" + base64url(url), padding trimmed.
static string EncodeShareId(string url)
{
    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
    return "u!" + b64.TrimEnd('=').Replace('/', '_').Replace('+', '-');
}
