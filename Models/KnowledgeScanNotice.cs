namespace NoisLogTray;

// What the tray does about the newest scan run, and the two run starts it then keeps in settings.json.
internal sealed record KnowledgeScanNotice(bool Notify, bool ShowBadge, DateTime JudgedRunAt, DateTime SeenRunAt);
