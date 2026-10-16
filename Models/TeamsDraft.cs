namespace NoisLogTray;

// Html carries the composer's markers: data-mention="i" spans, data-image="j" images, data-ams="id" images the message already had.
internal sealed record TeamsDraft(string Html, IReadOnlyList<TeamsMention> Mentions, IReadOnlyList<TeamsImage> Images);
