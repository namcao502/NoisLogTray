using System.Text.Json;

namespace NoisLogTray;

// Persisted user data (settings.json under %AppData%\NoisLogTray): UI state (theme,
// window position and size) plus the Config key/value map that AppConfig reads. Kept in one
// object so independent writers (theme toggle, window move, credential save) round-trip
// the whole thing and never clobber each other's keys. Writes are atomic (temp file +
// rename); a file that exists but cannot be parsed is preserved as settings.json.bad
// rather than silently overwritten with defaults.
internal sealed class AppSettings
{
    public bool Dark { get; set; } = true;
    public string Language { get; set; } = "en"; // "en" or "vi"
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool? WindowMaximized { get; set; } // null = never saved -> open maximized
    public bool SidebarCollapsed { get; set; }
    public int? TicketCheckMinutes { get; set; } // how often Jira and the PRs are checked; null = TicketCheckInterval.DefaultMinutes
    public DateTime? KnowledgeScanJudgedRunAt { get; set; } // start of the newest scan run the tray has judged; null = no baseline yet
    public DateTime? KnowledgeScanSeenRunAt { get; set; } // start of the newest bad scan run the Schedules dialog has shown
    public List<string> SeenWorkAttention { get; set; } = new(); // TasksPage.AttentionKeys the Work page has shown
    public Dictionary<string, string> Config { get; set; } = new();
    public List<string> OpenTicketTabs { get; set; } = new(); // ticket keys, in tab order
    public string? ClaudeDirectory { get; set; } // the single Terminal page's folder before tabs; read once to make the first tab
    public List<TerminalTabSetting>? TerminalTabs { get; set; } // in tab order; null = never saved (make the first tab), unlike []
    public string? SelectedTerminal { get; set; } // the Terminal page's selected tab
    public List<string> RecentClaudeDirectories { get; set; } = new(); // most recent first
    public Dictionary<string, string> ClaudeSessionIds { get; set; } = new(); // TerminalTabs.SessionKey -> the tab's last session there
    public Dictionary<string, DateTimeOffset> TeamsPostedAt { get; set; } = new(); // ticket key -> when its Teams post was sent
    public bool TicketHeaderCollapsed { get; set; } // the Ticket tab header shows only its key line
    public double? DiffListWidthEm { get; set; } // the Changes file list width in the diff page's em; null = default
    public bool DiffListHidden { get; set; }
    public bool DiffInline { get; set; } // the Changes diff in one column instead of two
    public double? FilesListWidthEm { get; set; } // the Files list width in the files page's em; null = default
    public bool FilesListHidden { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static AppSettings Load(string? path = null) => LoadOrBackup(out _, path);

    // A missing file yields defaults (corrupt=false). A file that exists but cannot be
    // parsed is copied aside to settings.json.bad - so the raw content, including any
    // credentials, is preserved for recovery - and defaults are returned with
    // corrupt=true. The primary file is never silently overwritten on a read failure.
    internal static AppSettings LoadOrBackup(out bool corrupt, string? path = null)
    {
        corrupt = false;
        var file = path ?? AppPaths.SettingsPath;
        if (!File.Exists(file)) return new AppSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options);
            if (parsed != null)
            {
                parsed.Config ??= new();
                parsed.OpenTicketTabs ??= new();
                parsed.RecentClaudeDirectories ??= new();
                parsed.ClaudeSessionIds ??= new();
                parsed.TeamsPostedAt ??= new();
                parsed.SeenWorkAttention ??= new();
                return parsed;
            }
        }
        catch
        {
            // fall through to backup + defaults
        }

        corrupt = true;
        TryBackupCorrupt(file);
        return new AppSettings();
    }

    private static void TryBackupCorrupt(string file)
    {
        try
        {
            File.Copy(file, file + ".bad", overwrite: true);
            AppLogger.Error($"settings.json was unreadable; preserved a copy at {file}.bad");
        }
        catch
        {
            // best effort
        }
    }

    internal static void Save(AppSettings settings, string? path = null)
    {
        try
        {
            var file = path ?? AppPaths.SettingsPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Write to a temp file then atomically replace, so an interrupted write can
            // never leave a truncated (unreadable) settings.json behind.
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // best effort; the setting just won't persist
        }
    }
}
