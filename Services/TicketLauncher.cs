using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NoisLogTray;

internal static class TicketLauncher
{
    private const string LegacyTaskLabel = "Resume Claude";

    // Kept as TaskWatcher's label so worktrees it already wrote are still recognised.
    private const string TaskLabelPrefix = "TaskWatcher:";

    private static readonly string ClaudeProjectsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    // Creates the worktree, drops a one-shot folderOpen task running `ship <TICKET>`, then opens VS Code.
    // Returns false (main repo opened, nothing launched) if the worktree can't be created.
    internal static bool StartNewTicket(string ticketKey, string repoPath, IProgress<StartStage>? progress = null)
    {
        var worktreePath = Path.Combine(repoPath, ".claude", "worktrees", ticketKey);

        // An existing worktree may already be open in VS Code, where folderOpen never fires,
        // so a ship task would linger and fire on a later reopen: give it only an idle Claude.
        if (Directory.Exists(worktreePath))
        {
            progress?.Report(StartStage.OpeningVsCode);
            TryWriteClaudeTask(worktreePath, GetIdlePrompt(worktreePath, ClaudeProjectsRoot));
            OpenVsCode(worktreePath);
            return true;
        }

        if (!WorktreeService.TryCreate(ticketKey, repoPath, worktreePath, progress))
        {
            progress?.Report(StartStage.OpeningVsCode);
            OpenVsCode(repoPath);
            return false;
        }

        TryWriteClaudeTask(worktreePath, $"ship {ticketKey}");

        progress?.Report(StartStage.OpeningVsCode);
        OpenVsCode(worktreePath);
        return true;
    }

    // Opens an existing worktree with an idle Claude (continuing its last session, if any);
    // falls back to the main repo, without Claude, if the worktree is missing.
    internal static void OpenExistingTicket(string ticketKey, string repoPath)
    {
        var worktreePath = Path.Combine(repoPath, ".claude", "worktrees", ticketKey);
        if (!Directory.Exists(worktreePath))
        {
            OpenVsCode(repoPath);
            return;
        }

        TryWriteClaudeTask(worktreePath, GetIdlePrompt(worktreePath, ClaudeProjectsRoot));
        OpenVsCode(worktreePath);
    }

    // A space-free prompt reaches the CLI unquoted, so "--continue" acts as a flag.
    // Only passed when a session exists: `claude --continue` with none just exits.
    internal static string? GetIdlePrompt(string worktreePath, string claudeProjectsRoot)
    {
        var sessionDirectory = Path.Combine(claudeProjectsRoot, GetClaudeProjectDirectoryName(worktreePath));

        // Lazy enumeration so Any() stops at the first session file.
        if (Directory.Exists(sessionDirectory) && Directory.EnumerateFiles(sessionDirectory, "*.jsonl").Any())
        {
            return "--continue";
        }

        return null;
    }

    // Claude Code stores sessions under ~/.claude/projects/<path with non-alphanumerics replaced by '-'>.
    internal static string GetClaudeProjectDirectoryName(string folderPath)
    {
        return Regex.Replace(folderPath, "[^a-zA-Z0-9]", "-");
    }

    // Never overwrites a tasks.json the user wrote; only ours or the legacy always-on "Resume Claude" one.
    // An IO failure is logged and VS Code still opens, so a created worktree is never left unopened.
    private static bool TryWriteClaudeTask(string worktreePath, string? prompt)
    {
        var vscodeDirectory = Path.Combine(worktreePath, ".vscode");
        var tasksFilePath = Path.Combine(vscodeDirectory, "tasks.json");
        try
        {
            if (File.Exists(tasksFilePath) && !IsTaskWatcherTaskFile(File.ReadAllText(tasksFilePath)))
            {
                AppLogger.Info($"[launch] {tasksFilePath} is the user's own; Claude task not written.");
                return false;
            }

            Directory.CreateDirectory(vscodeDirectory);
            File.WriteAllText(tasksFilePath, BuildClaudeTasksJson(prompt));
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[launch] Writing {tasksFilePath} failed: {e.Message}");
            return false;
        }
    }

    internal static bool IsTaskWatcherTaskFile(string tasksJson)
    {
        try
        {
            var root = JsonNode.Parse(tasksJson);
            var labels = (root?["tasks"] as JsonArray ?? new JsonArray())
                .Select(task => task?["label"]?.GetValue<string>() ?? string.Empty)
                .ToList();

            return labels.Count != 0
                && labels.All(label => label.StartsWith(TaskLabelPrefix, StringComparison.Ordinal) || label == LegacyTaskLabel);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            return false;
        }
    }

    // The URI handler ignores claudeCode.useTerminal, so `claude-vscode.terminal.open(prompt)` runs from a task input
    // (null prompt = idle Claude); the task deletes its own file so a later reopen does not run it again.
    internal static string BuildClaudeTasksJson(string? prompt)
    {
        var claudeInput = new JsonObject
        {
            ["id"] = "claudeTerminal",
            ["type"] = "command",
            ["command"] = "claude-vscode.terminal.open",
        };
        if (prompt != null)
        {
            claudeInput["args"] = prompt;
        }

        // The transient Claude terminal is restored as an empty locked group: join groups to drop it,
        // then unlock the survivor (the join may merge into the locked one), which holds the files.
        var joinGroupsInput = new JsonObject
        {
            ["id"] = "joinEditorGroups",
            ["type"] = "command",
            ["command"] = "workbench.action.joinAllGroups",
        };
        var unlockGroupInput = new JsonObject
        {
            ["id"] = "unlockEditorGroup",
            ["type"] = "command",
            ["command"] = "workbench.action.unlockEditorGroup",
        };

        var tasks = new JsonObject
        {
            ["version"] = "2.0.0",
            ["inputs"] = new JsonArray { joinGroupsInput, unlockGroupInput, claudeInput },
            ["tasks"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = $"{TaskLabelPrefix} {prompt ?? "open Claude"}",
                    ["type"] = "process",
                    ["command"] = "cmd.exe",
                    ["args"] = new JsonArray("/d", "/c", "del", @".vscode\tasks.json"),
                    // Only here to make VS Code resolve the inputs in order (join, unlock, open) before the delete.
                    ["options"] = new JsonObject
                    {
                        ["env"] = new JsonObject { ["TASKWATCHER_CLAUDE"] = "${input:joinEditorGroups}${input:unlockEditorGroup}${input:claudeTerminal}" },
                    },
                    ["runOptions"] = new JsonObject { ["runOn"] = "folderOpen" },
                    ["presentation"] = new JsonObject { ["reveal"] = "never", ["close"] = true },
                },
            },
        };

        return tasks.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // Inherited from a Claude Code session, these make the new Claude think it is a child session
    // and turn transcript saving off. ANTHROPIC_* come back from ~/.claude/settings.json.
    internal static bool IsClaudeSessionVariable(string name)
    {
        return name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenVsCode(string targetPath)
    {
        // `code` is a batch shim (code.cmd), unreliable as a direct process target: go through cmd.exe /c, hidden.
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c code \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var variableName in startInfo.Environment.Keys.Where(IsClaudeSessionVariable).ToList())
        {
            startInfo.Environment.Remove(variableName);
        }

        using var process = Process.Start(startInfo);
    }
}
