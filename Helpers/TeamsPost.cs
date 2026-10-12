using System.Text;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// The ticket's Teams post in the teams-post skill's format: "<KEY> <summary>", a Vietnamese description, then
// "[BE]" / "[FE <App>]" blocks of "main: <url>" / "dev: <url>". Text for the TextBox is always CRLF.
internal static class TeamsPost
{
    private const string NewLine = "\r\n";
    private static readonly Regex UrlPattern = new(@"https?://[^\s<>""']+", RegexOptions.Compiled);

    // The teams-post skill's header names per front-end repo.
    private static readonly Dictionary<string, string> FrontendAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Portal-Angular"] = "Portal",
        ["ExternalPortal-Angular"] = "External",
        ["LicensorPortal-Angular"] = "Licensor",
        ["MobileApp-Angular"] = "Mobile",
        ["WebApp-Angular"] = "WebApp",
    };

    // Title, a blank line, then the (empty) description line the user fills in; prs come ordered by LatestPrs.
    internal static string Build(string ticketKey, string? summary, IReadOnlyList<TicketPr> prs)
    {
        var builder = new StringBuilder(string.IsNullOrWhiteSpace(summary) ? ticketKey : $"{ticketKey} {summary.Trim()}");
        builder.Append(NewLine).Append(NewLine);
        foreach (var repositoryPrs in prs.Where(tp => tp.Status != "abandoned").GroupBy(tp => tp.Repository))
        {
            builder.Append(NewLine).Append(NewLine).Append(SectionHeader(repositoryPrs.First()));
            foreach (var pr in repositoryPrs) builder.Append(NewLine).Append($"{pr.Branch}: {pr.Url}");
        }
        return builder.ToString();
    }

    internal static string SectionHeader(TicketPr pr)
    {
        if (pr.RepoLabel == "BE") return "[BE]";
        if (pr.RepoLabel != "FE") return $"[{pr.RepoLabel}]";
        if (FrontendAppNames.TryGetValue(pr.Repository, out var appName)) return $"[FE {appName}]";
        return $"[FE {pr.Repository[..^"-Angular".Length]}]";
    }

    // The skill writes docs\<KEY>\teams-post.md with LF endings; worktree docs are mirrored to the dev repo, so it is checked first.
    internal static string? ReadDraft(string devRepoPath, string worktreePath, string ticketKey)
    {
        foreach (var root in new[] { devRepoPath, worktreePath })
        {
            var path = Path.Combine(root, "docs", ticketKey, "teams-post.md");
            if (!File.Exists(path)) continue;
            try
            {
                var draft = File.ReadAllText(path).Trim().ReplaceLineEndings(NewLine);
                if (draft.Length != 0) return draft;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                AppLogger.Error($"[teams] reading {path} failed: {e.Message}");
            }
        }
        return null;
    }

    // The first line goes in the channel post's Subject (plain text); the rest is the message.
    internal static (string Subject, string Body) SplitSubject(string text)
    {
        var parts = text.Trim().ReplaceLineEndings("\n").Split('\n', 2);
        return (parts[0].Trim(), parts.Length == 2 ? parts[1].Trim() : "");
    }

    // Teams renders the message as HTML.
    internal static string ToHtml(string body)
    {
        return string.Join("<br>", body.ReplaceLineEndings("\n").Split('\n').Select(LineToHtml));
    }

    // URLs are found on the raw line, so an escaped "&gt;" next to one never ends up inside its href.
    private static string LineToHtml(string line)
    {
        var html = new StringBuilder();
        var position = 0;
        foreach (Match url in UrlPattern.Matches(line))
        {
            // Prose punctuation right after a URL ("see https://x." or "(https://x)") is not part of it; a URL with "(" keeps its ")".
            var urlText = url.Value.Contains('(')
                ? url.Value.TrimEnd('.', ',', ';', ':', '!', '?', ']')
                : url.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
            html.Append(Escape(line[position..url.Index]));
            var escapedUrl = Escape(urlText);
            html.Append($"<a href=\"{escapedUrl}\">{escapedUrl}</a>");
            position = url.Index + urlText.Length;
        }
        return html.Append(Escape(line[position..])).ToString();
    }

    // Only the HTML-special characters: WebUtility.HtmlEncode would also turn Vietnamese letters into "&#224;".
    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // Where the description starts: right after the second line break (the title, then a blank line), else the end.
    // Counted in the text, not with TextBox.GetFirstCharIndexFromLine, which counts word-wrapped lines.
    internal static int DescriptionStart(string text)
    {
        var firstBreak = text.IndexOf(NewLine, StringComparison.Ordinal);
        var secondBreak = firstBreak < 0 ? -1 : text.IndexOf(NewLine, firstBreak + NewLine.Length, StringComparison.Ordinal);
        return secondBreak < 0 ? text.Length : secondBreak + NewLine.Length;
    }
}
