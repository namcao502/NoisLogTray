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
        Git("checkout", "-b", "main");
        WriteFile("README.md", "seed");
        Commit("seed");
        Git("push", "origin", "main");
    }

    // The folder holding both the clone and its origin.
    public string RootPath => _directory.Path;

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
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", message);
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
