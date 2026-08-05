namespace NoisLogTray;

// Typed runtime config. Reads a .env from the application directory (the project's
// .env, copied to the output on build); process environment is the last resort.
internal sealed class AppConfig
{
    // The "TSC Project" id (stable). Override via HRM_PROJECT_ID.
    private const string DefaultProjectId = "a72a7fb7-a8dc-4882-8da6-2031848a766f";

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
            Worksheet: env.Get("TSC_GRAPH_WORKSHEET"));
    }

    // Config lives with the app: a .env in the application directory (copied from
    // the project's .env on build). Process environment is the last-resort fallback
    // (see Env). No external paths.
    internal static string[] DefaultSources => new[]
    {
        Path.Combine(AppContext.BaseDirectory, ".env"),
    };

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
