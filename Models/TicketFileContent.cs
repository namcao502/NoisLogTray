namespace NoisLogTray;

// Content: the text for Text / Markdown, a data: URL for Image, null otherwise.
internal sealed record TicketFileContent(TicketFileKind Kind, string? Content);
