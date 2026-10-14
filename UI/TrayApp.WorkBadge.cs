namespace NoisLogTray;

// The Work row's badge counts tickets that newly need action (Ready to Test, Done in Jira) since the Work page last
// showed them; looking at the page clears it, and each row's status dot keeps marking what still waits.
internal sealed partial class TrayApp
{
    private IReadOnlyList<string> _workAttentionKeys = Array.Empty<string>();
    private HashSet<string>? _seenWorkAttention; // read from settings on first use

    private void OnWorkAttentionChanged(IReadOnlyList<string> attentionKeys)
    {
        _workAttentionKeys = attentionKeys;
        var isWatchingWork = _shell != null && !_shell.IsDisposed && _shell.Visible && _shell.IsForeground
            && _shell.CurrentPage == ShellPage.Work;
        if (isWatchingWork)
        {
            AcknowledgeWorkAttention();
        }
        else
        {
            ShowWorkBadge();
        }
    }

    // ShellForm.PageViewed: looking at the Work page counts as seeing every ticket that waits there now.
    private void AcknowledgeWorkAttention()
    {
        if (_shell == null || _shell.IsDisposed || _shell.CurrentPage != ShellPage.Work)
        {
            return;
        }

        if (!SeenWorkAttention().SetEquals(_workAttentionKeys))
        {
            // Replaced, not added to: a ticket that stops waiting drops out, so it counts as new if it waits again.
            _seenWorkAttention = _workAttentionKeys.ToHashSet();
            var settings = AppSettings.Load();
            settings.SeenWorkAttention = _workAttentionKeys.ToList();
            AppSettings.Save(settings);
        }
        ShowWorkBadge();
    }

    private void ShowWorkBadge()
    {
        if (_shell == null || _shell.IsDisposed)
        {
            return;
        }

        var seenWorkAttention = SeenWorkAttention();
        _shell.SetBadge(ShellPage.Work, _workAttentionKeys.Count(key => !seenWorkAttention.Contains(key)));
    }

    private HashSet<string> SeenWorkAttention() => _seenWorkAttention ??= AppSettings.Load().SeenWorkAttention.ToHashSet();
}
