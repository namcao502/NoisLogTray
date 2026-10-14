namespace NoisLogTray;

// One tab of the Terminal page as settings.json keeps it: its name (also its Claude hooks' session name) and folder.
internal sealed class TerminalTabSetting
{
    public string Name { get; set; } = "";
    public string? Directory { get; set; } // null = the dev repo
}
