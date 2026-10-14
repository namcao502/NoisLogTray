using System.Diagnostics;

namespace NoisLogTray.Tests;

// A throwaway clone whose origin is a local bare repo, with a seed commit on main already
// pushed, so `git fetch origin` and `origin/main` work offline.
public sealed class GitTestRepo : IDisposable
{
    private readonly TempDirectory _directory = new();

    public GitTestRepo()
    {
        var originPath = Path.Combine(_directory.Path, "origin.git");
        RepoPath = Path.Combine(_directory.Path, "repo");

        RunGit(_directory.Path, "init", "--bare", "--initial-branch=main", originPath);
        RunGit(_directory.Path, "clone", originPath, RepoPath);
        // CI has no global git identity; docs/ and .claude/ are ignored like in the real repo.
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        File.AppendAllText(Path.Combine(RepoPath, ".git", "info", "exclude"), "/docs/\n/.claude/\n");
        Git("checkout", "-b", "main");
        WriteFile("README.md", "seed");
        Commit("seed");
        Git("push", "origin", "main");
    }

    // The folder holding both the clone and its origin.
    public string RootPath
    {
        get { return _directory.Path; }
    }

    public string RepoPath { get; }

    public void Dispose()
    {
        _directory.Dispose();
    }

    public string Git(params string[] arguments)
    {
        return RunGit(RepoPath, arguments);
    }

    public void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(RepoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-m", message);
    }

    public static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output.Trim();
    }
}
