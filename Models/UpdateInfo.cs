namespace NoisLogTray;

// A newer release found on GitHub: its version (from the tag) and the release page URL
// to open in the browser. Only produced when Latest is greater than the running version.
internal sealed record UpdateInfo(Version Latest, string Url);
