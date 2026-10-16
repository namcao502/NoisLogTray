using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NoisLogTray;

// Playwright on the NOIS Chrome profile as the token source for TeamsChatClient: headless sniff, visible sign-in.
// Its own lock, so it can run beside a TSC sniff (different profile).
internal static class TeamsWebSniffer
{
    internal static readonly string ProfileDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nois-teams-browser");

    internal const string BusyMessage = "Teams sign-in or read already running";

    internal const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private const string TeamsUrl = "https://teams.cloud.microsoft/";
    private const string ChatAudience = "ic3.teams.office.com";
    private const string SpacesAudience = "https://api.spaces.skype.com";
    private const int SniffWaitSeconds = 45;
    private const int SignInPollTimeoutSeconds = 270;
    private const int FallbackTokenLifetimeMinutes = 30;

    private const string ChatTokenKey = "chatToken";
    private const string ChatUrlKey = "chatUrl";
    private const string AggregatorTokenKey = "aggregatorToken";
    private const string AggregatorUrlKey = "aggregatorUrl";
    private const string ClientVersionKey = "clientVersion";
    private const string ProfileTokenKey = "profileToken";

    private static readonly Regex ChatUrlPattern = new(@"^(https://[^/]+/api/chatsvc/[^/]+/v1/users/ME/)", RegexOptions.Compiled);
    private static readonly Regex ChatRegionPattern = new(@"/api/chatsvc/(?<region>[a-z0-9-]+)/", RegexOptions.Compiled);
    private static readonly Regex AggregatorUrlPattern = new(@"^(https://[^/]+/api/csa/[^/]+/)", RegexOptions.Compiled);

    private static int _profileHeld; // 0 = free, 1 = held

    internal static bool TryAcquireProfile()
    {
        return Interlocked.CompareExchange(ref _profileHeld, 1, 0) == 0;
    }

    internal static void ReleaseProfile()
    {
        Interlocked.Exchange(ref _profileHeld, 0);
    }

    internal static async Task<TeamsSniffResult> SniffAsync(CancellationToken cancellationToken)
    {
        if (!TryAcquireProfile())
        {
            return new TeamsSniffResult(null, false, BusyMessage);
        }

        try
        {
            if (!Directory.Exists(ProfileDir))
            {
                return new TeamsSniffResult(null, true, null);
            }

            if (!TscTokenSniffer.ChromeInstalled())
            {
                return new TeamsSniffResult(null, false, TscTokenSniffer.ChromeMissingMessage);
            }

            using var playwright = await Playwright.CreateAsync();
            await using var context = await playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, LaunchOptions(true));
            var page = context.Pages.Count != 0 ? context.Pages[0] : await context.NewPageAsync();

            var captured = new ConcurrentDictionary<string, string>();
            context.Request += (_, request) =>
            {
                RecordRequest(request, captured);
            };

            await page.GotoAsync(TeamsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

            string? userAgent = null;
            try
            {
                userAgent = await page.EvaluateAsync<string>("navigator.userAgent");
            }
            catch (PlaywrightException ex)
            {
                AppLogger.Info($"[teams] user agent not read: {ex.Message}");
            }

            var deadline = DateTime.UtcNow.AddSeconds(SniffWaitSeconds);
            while (DateTime.UtcNow < deadline && !(captured.ContainsKey(ChatTokenKey) && captured.ContainsKey(AggregatorTokenKey)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await page.WaitForTimeoutAsync(500);
            }

            captured.TryGetValue(ChatTokenKey, out var chatToken);
            captured.TryGetValue(ChatUrlKey, out var chatUrl);
            captured.TryGetValue(AggregatorTokenKey, out var aggregatorToken);
            captured.TryGetValue(AggregatorUrlKey, out var aggregatorUrl);

            captured.TryGetValue(ClientVersionKey, out var clientVersion);
            captured.TryGetValue(ProfileTokenKey, out var profileToken);

            var session = SessionFrom(chatToken, chatUrl, aggregatorToken, aggregatorUrl, DateTimeOffset.UtcNow, userAgent, clientVersion,
                profileToken);
            if (session != null)
            {
                AppLogger.Info("[teams] session captured");
                return new TeamsSniffResult(session, false, null);
            }

            if (chatToken != null && aggregatorToken != null)
            {
                return new TeamsSniffResult(null, false, "Teams token has no user id");
            }

            var pageUrl = page.Url;
            if (pageUrl.Contains("login.microsoftonline.com") || pageUrl.Contains("login.live.com"))
            {
                AppLogger.Info("[teams] saved sign-in is gone");
                return new TeamsSniffResult(null, true, null);
            }

            return new TeamsSniffResult(null, false, "Teams did not send the expected requests in time");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] sniff failed: {ex.Message}");
            return new TeamsSniffResult(null, false, ex.Message);
        }
        finally
        {
            ReleaseProfile();
        }
    }

    // The only visible browser flow: the user signs in to Teams web, and the session stays in the profile.
    internal static async Task<(bool Success, string? Error)> SignInAsync()
    {
        if (!TryAcquireProfile())
        {
            return (false, BusyMessage);
        }

        try
        {
            if (!TscTokenSniffer.ChromeInstalled())
            {
                return (false, TscTokenSniffer.ChromeMissingMessage);
            }

            using var playwright = await Playwright.CreateAsync();
            await using var context = await playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, LaunchOptions(false));
            var page = context.Pages.Count != 0 ? context.Pages[0] : await context.NewPageAsync();

            var captured = new ConcurrentDictionary<string, string>();
            context.Request += (_, request) =>
            {
                RecordRequest(request, captured);
            };

            await page.GotoAsync(TeamsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

            var deadline = DateTime.UtcNow.AddSeconds(SignInPollTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (page.IsClosed)
                {
                    return (false, "Browser closed before sign-in completed.");
                }

                if (captured.ContainsKey(ChatTokenKey))
                {
                    AppLogger.Info("[teams] sign-in detected");
                    return (true, null);
                }

                await page.WaitForTimeoutAsync(2000);
            }

            return (false, "Timed out waiting for the Teams sign-in.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] sign-in failed: {ex.Message}");
            return (false, ex.Message);
        }
        finally
        {
            ReleaseProfile();
        }
    }

    internal static TeamsSession? SessionFrom(string? chatToken, string? chatUrl, string? aggregatorToken, string? aggregatorUrl,
        DateTimeOffset now, string? userAgent = null, string? clientVersion = null, string? profileToken = null)
    {
        if (string.IsNullOrEmpty(chatToken) || string.IsNullOrEmpty(chatUrl)
            || string.IsNullOrEmpty(aggregatorToken) || string.IsNullOrEmpty(aggregatorUrl))
        {
            return null;
        }

        var chatMatch = ChatUrlPattern.Match(chatUrl);
        var aggregatorMatch = AggregatorUrlPattern.Match(aggregatorUrl);
        if (!chatMatch.Success || !aggregatorMatch.Success)
        {
            return null;
        }

        var objectId = GraphTscClient.DecodeJwtClaim(chatToken, "oid");
        if (string.IsNullOrEmpty(objectId))
        {
            return null;
        }

        var myId = ("8:orgid:" + objectId).ToLowerInvariant();
        var myName = GraphTscClient.DecodeJwtClaim(chatToken, "name") ?? "";

        // A token whose exp cannot be read still works, but counts as short-lived.
        var fallbackExpiry = now.AddMinutes(FallbackTokenLifetimeMinutes);
        var chatExpiry = GraphTscClient.DecodeJwtExpiry(chatToken) ?? fallbackExpiry;
        var aggregatorExpiry = GraphTscClient.DecodeJwtExpiry(aggregatorToken) ?? fallbackExpiry;
        var expiresAt = chatExpiry < aggregatorExpiry ? chatExpiry : aggregatorExpiry;

        // A HeadlessChrome user agent is refused by the Teams object store (400), so it is presented as plain Chrome.
        var normalizedUserAgent = string.IsNullOrWhiteSpace(userAgent)
            ? DefaultUserAgent
            : userAgent.Replace("HeadlessChrome", "Chrome", StringComparison.Ordinal);

        string? profilePictureBaseUrl = null;
        DateTimeOffset? profileTokenExpiresAt = null;
        var regionMatch = ChatRegionPattern.Match(chatMatch.Groups[1].Value);
        if (!string.IsNullOrEmpty(profileToken) && regionMatch.Success)
        {
            profilePictureBaseUrl = $"https://teams.cloud.microsoft/api/mt/{regionMatch.Groups["region"].Value}/beta/users/{objectId}/profilepicturev2/";
            profileTokenExpiresAt = GraphTscClient.DecodeJwtExpiry(profileToken);
        }
        else
        {
            profileToken = null;
        }

        return new TeamsSession(chatToken, aggregatorToken, chatMatch.Groups[1].Value, aggregatorMatch.Groups[1].Value, myId, myName,
            expiresAt, normalizedUserAgent, string.IsNullOrEmpty(clientVersion) ? null : clientVersion,
            profileToken, profilePictureBaseUrl, profileTokenExpiresAt);
    }

    private static void RecordRequest(IRequest request, ConcurrentDictionary<string, string> captured)
    {
        if (request.Headers.TryGetValue("x-ms-client-version", out var clientVersion) && !string.IsNullOrEmpty(clientVersion))
        {
            captured.TryAdd(ClientVersionKey, clientVersion);
        }

        if (!request.Headers.TryGetValue("authorization", out var authorization)
            || !authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var token = authorization.Substring(7);
        var audience = GraphTscClient.DecodeJwtClaim(token, "aud") ?? "";
        if (audience == SpacesAudience)
        {
            captured.TryAdd(ProfileTokenKey, token);
            return;
        }

        if (ChatUrlPattern.IsMatch(request.Url))
        {
            if (audience.Contains(ChatAudience))
            {
                captured[ChatTokenKey] = token;
                captured[ChatUrlKey] = request.Url;
            }
        }
        else if (AggregatorUrlPattern.IsMatch(request.Url))
        {
            captured[AggregatorTokenKey] = token;
            captured[AggregatorUrlKey] = request.Url;
        }
    }

    private static BrowserTypeLaunchPersistentContextOptions LaunchOptions(bool headless)
    {
        return new BrowserTypeLaunchPersistentContextOptions
        {
            Channel = "chrome",
            Headless = headless,
            ServiceWorkers = ServiceWorkerPolicy.Block,
            Args = new[] { "--disable-blink-features=AutomationControlled" },
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        };
    }
}
