namespace NoisLogTray.Tests;

public class TicketWorktreesTests : IDisposable
{
    private readonly TempDirectory _projects = new();

    public void Dispose()
    {
        _projects.Dispose();
    }

    [Fact]
    public void Find_ReturnsBackendThenAngularWorktrees()
    {
        var backendPath = CreateWorktree("Backend-DotNet", "MDP-1");
        var portalPath = CreateWorktree("Portal-Angular", "MDP-1");
        Directory.CreateDirectory(Path.Combine(_projects.Path, "WebApp-Angular"));

        var worktrees = TicketWorktrees.Find(Path.Combine(_projects.Path, "Backend-DotNet"), "MDP-1");

        Assert.Equal(new[] { "Backend-DotNet", "Portal-Angular" }, worktrees.Select(tw => tw.Repository));
        Assert.Equal(new[] { "BE", "FE" }, worktrees.Select(tw => tw.RepoLabel));
        Assert.Equal(new[] { backendPath, portalPath }, worktrees.Select(tw => tw.Path));
    }

    [Fact]
    public void Find_LabelsTheDevRepoBackend_WhateverItsFolderName()
    {
        CreateWorktree("Backend", "MDP-1");

        var worktrees = TicketWorktrees.Find(Path.Combine(_projects.Path, "Backend"), "MDP-1");

        var worktree = Assert.Single(worktrees);
        Assert.Equal("Backend-DotNet", worktree.Repository);
        Assert.Equal("BE", worktree.RepoLabel);
    }

    [Fact]
    public void Find_ReturnsEmpty_WhenNoWorktree()
    {
        Directory.CreateDirectory(Path.Combine(_projects.Path, "Backend-DotNet"));
        CreateWorktree("Portal-Angular", "MDP-2");

        var worktrees = TicketWorktrees.Find(Path.Combine(_projects.Path, "Backend-DotNet"), "MDP-1");

        Assert.Empty(worktrees);
    }

    private string CreateWorktree(string repositoryFolder, string ticketKey)
    {
        var path = Path.Combine(_projects.Path, repositoryFolder, ".claude", "worktrees", ticketKey);
        Directory.CreateDirectory(path);
        return path;
    }
}
