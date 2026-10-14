namespace NoisLogTray;

// A Claude session waiting for the user, as the Dashboard lists it. Label is the notice's: a ticket key or "Terminal <folder>".
internal sealed record ClaudeWait(string SessionName, string Label, ClaudeWaitReason Reason);
