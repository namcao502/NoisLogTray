namespace NoisLogTray;

// Unseen new PR comments on one ticket; PrUrl is the most recently commented PR.
internal sealed record PrCommentAlert(string TicketKey, int NewCommentCount, string PrUrl, DateTimeOffset LastAtHcm);
