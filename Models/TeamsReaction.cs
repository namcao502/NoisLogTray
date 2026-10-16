namespace NoisLogTray;

// One reaction on a Teams message: Teams' emoji key ("like", "smilingfacewithtear") and who gave it.
internal sealed record TeamsReaction(string Key, IReadOnlyList<string> UserMris);
