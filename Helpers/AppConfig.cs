using System.Globalization;

namespace NoisLogTray;

// Typed runtime config, read from the Config map in settings.json (%AppData%). A
// legacy per-user .env is migrated into it once on load; process env is the last resort.
internal sealed class AppConfig
{
    // The "TSC Project" id (stable). Override via HRM_PROJECT_ID.
    private const string DefaultProjectId = "a72a7fb7-a8dc-4882-8da6-2031848a766f";

    // Team default so the first-run dialog can pre-fill the (non-secret) Jira site.
    internal const string DefaultJiraBaseUrl = "https://newoceaninfosys.atlassian.net";

    // Daily time (Asia/Ho_Chi_Minh) for the auto-log drain and the empty-queue reminder.
    internal static readonly TimeOnly DefaultLogTime = new(18, 0);

    // Accepted LOG_TIME input formats: 12-hour with AM/PM first, 24-hour still allowed.
    internal static readonly string[] LogTimeFormats = { "h:mm tt", "hh:mm tt", "H:mm", "HH:mm" };

    // Keys the first-run / edit-credentials dialog manages in settings.json's Config.
    internal static readonly string[] UserKeys =
    {
        "JIRA_BASE_URL", "JIRA_EMAIL", "JIRA_API_TOKEN", "HRM_API_KEY", "TSC_GRAPH_COLUMNS", "LOG_TIME",
    };

    internal string JiraBaseUrl { get; }
    internal string JiraEmail { get; }
    internal string JiraToken { get; }
    internal string HrmApiKey { get; }
    internal string HrmProjectId { get; }
    internal string? MsGraphToken { get; }
    internal TimeOnly LogTime { get; }
    internal GraphTscOptions Graph { get; }

    private AppConfig(Env env)
    {
        JiraBaseUrl = env.Require("JIRA_BASE_URL");
        JiraEmail = env.Require("JIRA_EMAIL");
        JiraToken = env.Require("JIRA_API_TOKEN");
        HrmApiKey = env.Require("HRM_API_KEY");
        HrmProjectId = env.Get("HRM_PROJECT_ID") ?? DefaultProjectId;
        MsGraphToken = env.Get("MS_GRAPH_TOKEN");
        LogTime = ParseLogTime(env.Get("LOG_TIME"));
        Graph = new GraphTscOptions(
            DriveId: env.Get("TSC_GRAPH_DRIVE_ID"),
            ItemId: env.Get("TSC_GRAPH_ITEM_ID"),
            ShareUrl: env.Get("TSC_GRAPH_SHARE_URL"),
            Worksheet: env.Get("TSC_GRAPH_WORKSHEET"),
            Columns: TscCells.ParseColumns(env.Get("TSC_GRAPH_COLUMNS")));
    }

    // Parse the daily log time (12-hour "h:mm tt" or 24-hour "H:mm"); fall back to
    // 18:00 on anything unrecognised so a bad value can never break startup.
    internal static TimeOnly ParseLogTime(string? value)
    {
        if (value != null && TimeOnly.TryParseExact(value.Trim(), LogTimeFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return time;
        return DefaultLogTime;
    }

    // Current effective values for the managed keys, for pre-filling the edit dialog.
    internal static IReadOnlyDictionary<string, string> ReadUserValues()
    {
        var env = new Env(Decrypted(AppSettings.Load().Config));
        var values = new Dictionary<string, string>();
        foreach (var key in UserKeys)
        {
            var value = env.Get(key);
            if (value != null) values[key] = value;
        }
        return values;
    }

    // Merge the given keys into settings.json's Config (read-modify-write so the theme,
    // window position, and any other config keys are preserved) and persist atomically.
    internal static void SaveUserConfig(IReadOnlyDictionary<string, string> values)
    {
        var settings = AppSettings.Load();
        foreach (var kv in values)
            settings.Config[kv.Key] = Secrets.IsSecretKey(kv.Key) ? Secrets.Protect(kv.Value) : kv.Value;
        AppSettings.Save(settings);
    }

    // Load config, or return null with a message if a required key is missing so the
    // tray can surface it instead of crashing at startup.
    internal static AppConfig? TryLoad(out string? error)
    {
        var settings = AppSettings.LoadOrBackup(out var corrupt);
        MigrateLegacyEnv(settings);
        UpgradeSecretsAtRest(settings);
        try
        {
            var config = new AppConfig(new Env(Decrypted(settings.Config)));
            error = null;
            return config;
        }
        catch (Exception e)
        {
            error = corrupt
                ? "Settings were unreadable and have been backed up to settings.json.bad. Please re-enter your credentials."
                : e.Message;
            return null;
        }
    }

    // One-time migration: fold an old-style per-user .env into settings.json's Config
    // (without overwriting keys already present), persist, then delete the .env so all
    // config lives in one file going forward.
    private static void MigrateLegacyEnv(AppSettings settings)
    {
        try
        {
            var legacy = AppPaths.EnvPath;
            if (!File.Exists(legacy)) return;

            var parsed = Env.ParseFile(legacy);
            foreach (var kv in parsed)
                if (!settings.Config.ContainsKey(kv.Key))
                    settings.Config[kv.Key] = Secrets.IsSecretKey(kv.Key) ? Secrets.Protect(kv.Value) : kv.Value;

            AppSettings.Save(settings);
            File.Delete(legacy);
            AppLogger.Info($"Migrated {parsed.Count} config key(s) from legacy .env into settings.json.");
        }
        catch (Exception e)
        {
            AppLogger.Error($"Legacy .env migration failed: {e.Message}");
        }
    }

    // A copy of the stored config with secret values decrypted, for building the
    // runtime Env. Non-secret keys pass through unchanged.
    private static Dictionary<string, string> Decrypted(IReadOnlyDictionary<string, string> config)
    {
        var result = new Dictionary<string, string>(config.Count);
        foreach (var kv in config)
            result[kv.Key] = Secrets.IsSecretKey(kv.Key) ? Secrets.Unprotect(kv.Value) : kv.Value;
        return result;
    }

    // One-time upgrade: encrypt any secret values still stored as plaintext (e.g.
    // migrated before encryption existed) so they are protected at rest from now on.
    private static void UpgradeSecretsAtRest(AppSettings settings)
    {
        var changed = false;
        foreach (var key in Secrets.Keys)
            if (settings.Config.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) && !Secrets.IsProtected(v))
            {
                settings.Config[key] = Secrets.Protect(v);
                changed = true;
            }
        if (changed) AppSettings.Save(settings);
    }
}
