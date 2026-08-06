namespace NoisLogTray;

// Typed runtime config. Reads a .env from the application directory (the project's
// .env, copied to the output on build); process environment is the last resort.
internal sealed class AppConfig
{
    // The "TSC Project" id (stable). Override via HRM_PROJECT_ID.
    private const string DefaultProjectId = "a72a7fb7-a8dc-4882-8da6-2031848a766f";

    // Team default so the first-run dialog can pre-fill the (non-secret) Jira site.
    internal const string DefaultJiraBaseUrl = "https://newoceaninfosys.atlassian.net";

    // Keys the first-run / edit-credentials dialog manages in the per-user .env.
    internal static readonly string[] UserKeys =
    {
        "JIRA_BASE_URL", "JIRA_EMAIL", "JIRA_API_TOKEN", "HRM_API_KEY", "TSC_GRAPH_COLUMNS",
    };

    internal string JiraBaseUrl { get; }
    internal string JiraEmail { get; }
    internal string JiraToken { get; }
    internal string HrmApiKey { get; }
    internal string HrmProjectId { get; }
    internal string? MsGraphToken { get; }
    internal GraphTscOptions Graph { get; }

    private AppConfig(Env env)
    {
        JiraBaseUrl = env.Require("JIRA_BASE_URL");
        JiraEmail = env.Require("JIRA_EMAIL");
        JiraToken = env.Require("JIRA_API_TOKEN");
        HrmApiKey = env.Require("HRM_API_KEY");
        HrmProjectId = env.Get("HRM_PROJECT_ID") ?? DefaultProjectId;
        MsGraphToken = env.Get("MS_GRAPH_TOKEN");
        Graph = new GraphTscOptions(
            DriveId: env.Get("TSC_GRAPH_DRIVE_ID"),
            ItemId: env.Get("TSC_GRAPH_ITEM_ID"),
            ShareUrl: env.Get("TSC_GRAPH_SHARE_URL"),
            Worksheet: env.Get("TSC_GRAPH_WORKSHEET"),
            Columns: TscCells.ParseColumns(env.Get("TSC_GRAPH_COLUMNS")));
    }

    // Layered config: the app-directory .env holds optional shared, non-secret
    // defaults; the per-user .env in %AppData% holds each user's secrets and
    // overrides it (later files win in Env). Process environment is the last resort.
    internal static string[] DefaultSources => new[]
    {
        Path.Combine(AppContext.BaseDirectory, ".env"),
        AppPaths.EnvPath,
    };

    // Current effective values for the managed keys, for pre-filling the edit dialog.
    internal static IReadOnlyDictionary<string, string> ReadUserValues()
    {
        var env = new Env(DefaultSources);
        var values = new Dictionary<string, string>();
        foreach (var key in UserKeys)
        {
            var value = env.Get(key);
            if (value != null) values[key] = value;
        }
        return values;
    }

    // Merge the given keys into the per-user .env (keeping any other keys already
    // there) and write it back, creating the data directory if needed.
    internal static void SaveUserEnv(IReadOnlyDictionary<string, string> values)
    {
        var path = AppPaths.EnvPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var merged = Env.ParseFile(path);
        foreach (var kv in values) merged[kv.Key] = kv.Value;

        var lines = merged.Select(kv => $"{kv.Key}={kv.Value}");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    // Load config, or return null with a message if a required key is missing so
    // the tray can surface it instead of crashing at startup.
    internal static AppConfig? TryLoad(out string? error)
    {
        try
        {
            var config = new AppConfig(new Env(DefaultSources));
            error = null;
            return config;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }
}
