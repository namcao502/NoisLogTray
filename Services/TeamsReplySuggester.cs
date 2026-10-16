namespace NoisLogTray;

// Asks `claude -p` to analyze a Teams conversation; the replies only fill cards and the editor, so the user reviews each before sending.
internal static class TeamsReplySuggester
{
    private static readonly TimeSpan SuggestTimeout = TimeSpan.FromMinutes(5);
    private const int MaxErrorChars = 300;

    internal static Task<(TeamsAnalysis? Analysis, string? Error)> SuggestAsync(string workingDirectory, string prompt,
        IReadOnlyList<string> extraDirectories, CancellationToken cancellationToken)
    {
        return Task.Run<(TeamsAnalysis? Analysis, string? Error)>(() =>
        {
            // Read-only tools: the thread holds other people's text. The prompt goes over stdin, as a long thread would pass the command line's limit.
            var arguments = new List<string> { "-p", "--output-format", "json", "--allowedTools", "Read,Grep,Glob" };
            foreach (var extraDirectory in extraDirectories)
            {
                arguments.Add("--add-dir");
                arguments.Add(extraDirectory);
            }

            var (exitCode, output, error) = ClaudeCli.Run(workingDirectory, arguments, prompt, SuggestTimeout, cancellationToken);

            string failure;
            if (exitCode != 0)
            {
                failure = error.Length != 0 ? error : output;
            }
            else
            {
                var (succeeded, resultText) = KnowledgeMiner.ParseResult(output);
                var analysis = succeeded ? TeamsReply.ParseAnalysis(resultText) : null;
                if (analysis != null)
                {
                    return (analysis, null);
                }

                failure = succeeded ? Lang.T("Claude returned no analysis.", "Claude không trả về phân tích.") : resultText;
            }

            var oneLineFailure = string.Join(' ', failure.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (oneLineFailure.Length == 0)
            {
                oneLineFailure = $"claude exited with {exitCode}";
            }

            if (oneLineFailure.Length > MaxErrorChars)
            {
                oneLineFailure = oneLineFailure[..MaxErrorChars];
            }

            // The failure text can quote the thread, so the log gets only its size.
            AppLogger.Error($"[teams] analysis failed (exit {exitCode}, {oneLineFailure.Length} chars of error)");
            return (null, oneLineFailure);
        }, CancellationToken.None);
    }
}
