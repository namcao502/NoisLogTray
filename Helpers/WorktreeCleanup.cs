using System.Text.RegularExpressions;

namespace NoisLogTray;

internal static class WorktreeCleanup
{
    internal static readonly string[] LocalOnlyFiles = ["src/docker-compose.yml", "src/environments/environment.ts"];

    private const string WorktreesFolderMarker = @"\.claude\worktrees\";

    private static readonly Regex MiningLine = new(@"\bMining (\S+) ", RegexOptions.Compiled);
    private static readonly Regex GivingUpLine = new(@"\bGiving up on (\S+)", RegexOptions.Compiled);
    private static readonly Regex TicketKeyInText = new(@"\b[A-Z][A-Z0-9]*-[0-9]+\b", RegexOptions.Compiled);
    private static readonly string[] SlimFolderNames = ["attachments", ".pr-scratch"];
    private static readonly string[] SlimFileNames = ["plan-review.md", "impl-review.md", "test-plan.md", ".issue.json"];
    private static readonly string[] KeptDataFileNames = ["migrate.sql", "Translation.sql", "Schema_Migrate.sql", "revert.sql"];

    // The ticket whose worktree holds this folder (".claude\worktrees\<KEY>" or below it); null outside every worktree.
    internal static string? WorktreeKeyOf(string directory)
    {
        var markerIndex = directory.IndexOf(WorktreesFolderMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex == -1) return null;
        var afterMarker = directory[(markerIndex + WorktreesFolderMarker.Length)..];
        var separatorIndex = afterMarker.IndexOfAny(['\\', '/']);
        return separatorIndex == -1 ? afterMarker : afterMarker[..separatorIndex];
    }

    internal static HashSet<string> MinedTickets(string scanLog, string retryFile)
    {
        var minedTicketKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in scanLog.Split('\n'))
        {
            var miningMatch = MiningLine.Match(line);
            if (miningMatch.Success)
            {
                minedTicketKeys.Add(miningMatch.Groups[1].Value);
                continue;
            }

            var givingUpMatch = GivingUpLine.Match(line);
            if (givingUpMatch.Success)
            {
                minedTicketKeys.Remove(givingUpMatch.Groups[1].Value);
            }
        }

        foreach (var line in retryFile.TrimStart('\uFEFF').Split('\n'))
        {
            var retryKey = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (retryKey != null)
            {
                minedTicketKeys.Remove(retryKey);
            }
        }

        return minedTicketKeys;
    }

    // Keys named in merge commit subjects ("Merged PR 123: MDP-1 ..."), one subject per line.
    internal static HashSet<string> MergedTickets(string mergeSubjects)
    {
        var mergedTicketKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match keyMatch in TicketKeyInText.Matches(mergeSubjects))
        {
            mergedTicketKeys.Add(keyMatch.Value);
        }

        return mergedTicketKeys;
    }

    internal static (CleanupVerdict Verdict, List<string> Reasons) Classify(WorktreeFacts facts)
    {
        if (facts.InspectFailed)
        {
            return (CleanupVerdict.Keep, [Lang.T("could not read git (see log)", "không đọc được git (xem log)")]);
        }

        if (facts.TerminalRunning)
        {
            return (CleanupVerdict.Keep, [Lang.T("its terminal is running", "terminal của nó đang chạy")]);
        }

        if (facts.UnpushedCommits > 0)
        {
            return (CleanupVerdict.Keep, [Lang.T(
                $"{facts.UnpushedCommits} commit(s) not on the remote",
                $"{facts.UnpushedCommits} commit chưa có trên remote")]);
        }

        var reasons = new List<string>();

        if (facts.IsBroken)
        {
            reasons.Add(Lang.T("worktree is broken (half removed)", "worktree hỏng (xoá dở)"));
        }

        if (facts.DirtyFiles > 0)
        {
            reasons.Add(Lang.T(
                $"{facts.DirtyFiles} uncommitted file(s) will be deleted",
                $"{facts.DirtyFiles} file chưa commit sẽ bị xoá"));
        }

        if (facts.LocalConfigDiffs > 0)
        {
            reasons.Add(Lang.T(
                "docs\\local-config differs from the main checkout",
                "docs\\local-config khác với bản ở checkout chính"));
        }

        if (facts.InProgress)
        {
            reasons.Add(Lang.T("In progress on the board", "đang In progress trên board"));
        }

        if (facts.OtherBranch != null)
        {
            reasons.Add(Lang.T(
                $"worktree is on branch {facts.OtherBranch}",
                $"worktree đang ở nhánh {facts.OtherBranch}"));
        }

        var shipped = facts.HasTrackedPrs ? facts.AllPrsClosed && facts.AnyPrCompleted : facts.BranchMerged;
        if (!shipped)
        {
            if (facts.HasTrackedPrs && facts.AllPrsClosed && !facts.AnyPrCompleted)
            {
                reasons.Add(Lang.T("PR abandoned, never merged", "PR bị abandon, chưa merge"));
            }
            else if (facts.MainPrMerged)
            {
                reasons.Add(Lang.T("merged to main, a PR is still open", "đã merge main, còn PR mở"));
            }
            else
            {
                reasons.Add(Lang.T("not merged to main yet", "chưa merge vào main"));
            }
        }

        var verdict = reasons.Count != 0 ? CleanupVerdict.Check : CleanupVerdict.Ready;
        return (verdict, reasons);
    }

    internal static List<string> SlimTargets(string ticketDocsPath)
    {
        var slimTargetPaths = new List<string>();
        if (!Directory.Exists(ticketDocsPath))
        {
            return slimTargetPaths;
        }

        var docsRootPaths = new List<string> { ticketDocsPath };
        var bugsPath = Path.Combine(ticketDocsPath, "bugs");
        if (Directory.Exists(bugsPath))
        {
            docsRootPaths.AddRange(Directory.GetDirectories(bugsPath));
        }

        foreach (var docsRootPath in docsRootPaths)
        {
            // The bugs tree is handled per bug folder.
            var skippedFolderPaths = new List<string> { Path.Combine(docsRootPath, "bugs") };

            foreach (var folderName in SlimFolderNames)
            {
                var folderPath = Path.Combine(docsRootPath, folderName);
                if (Directory.Exists(folderPath))
                {
                    slimTargetPaths.Add(folderPath);
                    skippedFolderPaths.Add(folderPath);
                }
            }

            foreach (var fileName in SlimFileNames)
            {
                var filePath = Path.Combine(docsRootPath, fileName);
                if (File.Exists(filePath))
                {
                    slimTargetPaths.Add(filePath);
                }
            }

            foreach (var filePath in Directory.EnumerateFiles(docsRootPath, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(filePath);
                var isDataFile = extension.Equals(".sql", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
                if (!isDataFile)
                {
                    continue;
                }

                var isKept = KeptDataFileNames.Contains(Path.GetFileName(filePath), StringComparer.OrdinalIgnoreCase);
                var isSkipped = skippedFolderPaths.Any(sfp => filePath.StartsWith(sfp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (!isKept && !isSkipped)
                {
                    slimTargetPaths.Add(filePath);
                }
            }
        }

        return slimTargetPaths;
    }
}
