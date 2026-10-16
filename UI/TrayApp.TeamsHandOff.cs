using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// "Hand to Claude": a task file for someone else's Teams message, and a line in the ticket terminal that points claude at it.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan HandOffKeepTime = TimeSpan.FromDays(7);
    private static readonly Regex HandOffShotsDirectoryPattern = new(@"^\d{8}-\d{6}$", RegexOptions.Compiled);

    // Sessions whose claude ended inside the PowerShell (SessionEnd, not /clear); read and written on the UI thread.
    private readonly HashSet<string> _claudeEndedInShell = new();

    // Ticket key -> the line its not yet running terminal starts claude with.
    private readonly Dictionary<string, string> _pendingHandOffs = new();

    // Null when handed over, else the error text for the page.
    private async Task<string?> HandToClaudeAsync(string ticketKey, TeamsThread thread, string messageId, CancellationToken cancellationToken)
    {
        if (_shipPending.Contains(ticketKey))
        {
            return Lang.T("The terminal still has to run ship first; open Claude.", "Terminal còn phải chạy ship trước; mở Claude đi.");
        }

        var message = thread.Messages.FirstOrDefault(tm => tm.Id == messageId);
        if (message == null)
        {
            return Lang.T("That message is no longer in the thread.", "Tin này không còn trong thread.");
        }

        var myId = await _teamsChat.GetMyIdAsync(cancellationToken);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        IReadOnlyList<TeamsScreenshot> screenshots = TeamsChat.ImageUrls(message).Take(TeamsReply.MaxScreenshots)
            .Select(url => new TeamsScreenshot(url, message.SenderName, message.SentAt)).ToList();
        if (screenshots.Count != 0)
        {
            screenshots = await _teamsThreads.SaveImagesAsync(screenshots, Path.Combine(AppPaths.TeamsShotsDir(ticketKey), stamp), cancellationToken);
        }

        var taskPath = Path.Combine(AppPaths.TeamsTasksDir, $"{ticketKey}-{stamp}.md");
        try
        {
            Directory.CreateDirectory(AppPaths.TeamsTasksDir);
            File.WriteAllText(taskPath, TeamsReply.BuildClaudeTask(ticketKey, FindTicketSummary(ticketKey), thread, messageId, myId, screenshots),
                new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[teams] {ticketKey}: writing the task file failed: {e.Message}");
            return Lang.T("Could not write the task file (see log).", "Không ghi được file nhiệm vụ (xem log).");
        }

        DeleteOldHandOffFiles(ticketKey);
        return await SendLineToClaude(ticketKey, TeamsHandOff.Line(taskPath), cancellationToken);
    }

    // Types one line into the ticket's Claude: at its prompt, at the shell prompt, or at the next start. Null when sent, else the error text.
    private async Task<string?> SendLineToClaude(string ticketKey, string line, CancellationToken cancellationToken)
    {
        if (_shipPending.Contains(ticketKey))
        {
            return Lang.T("The terminal still has to run ship first; open Claude.", "Terminal còn phải chạy ship trước; mở Claude đi.");
        }

        var host = SessionFor(ticketKey);
        var target = TeamsHandOff.Target(host.IsRunning, _claudeEndedInShell.Contains(ticketKey));
        AppLogger.Info($"[teams] {ticketKey}: sent a line to Claude ({target})");
        if (target == TeamsHandOffTarget.ClaudePrompt)
        {
            await PasteAndSubmitAsync(ticketKey, host, line, cancellationToken);
        }
        else if (target == TeamsHandOffTarget.ShellPrompt)
        {
            var idlePrompt = TicketLauncher.GetIdlePrompt(BackendWorktreePath(ticketKey), TicketLauncher.ClaudeProjectsRoot);
            host.Write(Encoding.UTF8.GetBytes(TeamsHandOff.ShellCommand(idlePrompt, line)));
        }
        else
        {
            _pendingHandOffs[ticketKey] = line;
        }

        ShowTicketTab(ticketKey);
        _ticketPage?.ShowClaude(ticketKey);
        return null;
    }

    // Enter is sent after a pause so claude has taken the paste, and only if the terminal still runs.
    private async Task PasteAndSubmitAsync(string ticketKey, ClaudeSessionHost host, string line, CancellationToken cancellationToken)
    {
        try
        {
            host.Write(Encoding.UTF8.GetBytes(TeamsHandOff.PasteInput(line)));
            await Task.Delay(300, cancellationToken);
            if (host.IsRunning)
            {
                host.Write(Encoding.UTF8.GetBytes("\r"));
            }
        }
        catch (Exception e) when (e is ObjectDisposedException || e is OperationCanceledException)
        {
            AppLogger.Info($"[teams] {ticketKey}: the hand-off did not finish, the terminal closed");
        }
    }

    private void DeleteOldHandOffFiles(string ticketKey)
    {
        var cutoff = DateTime.UtcNow - HandOffKeepTime;
        try
        {
            foreach (var oldFile in Directory.GetFiles(AppPaths.TeamsTasksDir).Where(path => File.GetLastWriteTimeUtc(path) < cutoff))
            {
                File.Delete(oldFile);
            }

            var shotsDirectory = AppPaths.TeamsShotsDir(ticketKey);
            if (Directory.Exists(shotsDirectory))
            {
                foreach (var oldDirectory in Directory.GetDirectories(shotsDirectory)
                    .Where(path => HandOffShotsDirectoryPattern.IsMatch(Path.GetFileName(path)) && Directory.GetLastWriteTimeUtc(path) < cutoff))
                {
                    Directory.Delete(oldDirectory, true);
                }
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[teams] {ticketKey}: cleaning old hand-off files failed: {e.Message}");
        }
    }

    private void DropTeamsHandOff(string ticketKey)
    {
        _pendingHandOffs.Remove(ticketKey);
        _claudeEndedInShell.Remove(ticketKey);
    }

    // `ship` first, then a waiting hand-off, then the idle prompt (--continue when the worktree has a session).
    private string? TicketSessionArgument(string ticketKey)
    {
        if (_shipPending.Contains(ticketKey))
        {
            return $"ship {ticketKey}";
        }

        var idlePrompt = TicketLauncher.GetIdlePrompt(BackendWorktreePath(ticketKey), TicketLauncher.ClaudeProjectsRoot);
        return _pendingHandOffs.TryGetValue(ticketKey, out var line) ? TeamsHandOff.StartArgument(idlePrompt, line) : idlePrompt;
    }
}
