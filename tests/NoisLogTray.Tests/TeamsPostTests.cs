namespace NoisLogTray.Tests;

public class TeamsPostTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("NoisLogTray.TeamsPost").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private string WriteDraft(string repoName, string content)
    {
        var repoPath = Directory.CreateDirectory(Path.Combine(_root, repoName)).FullName;
        var draftFolder = Directory.CreateDirectory(Path.Combine(repoPath, "docs", "MDP-1")).FullName;
        File.WriteAllText(Path.Combine(draftFolder, "teams-post.md"), content);
        return repoPath;
    }

    [Fact]
    public void Build_ListsBackendThenEachFrontendRepo_WhenPrsExist()
    {
        var backendMain = new TicketPr("Backend-DotNet", "main", 1, "active", 0);
        var backendDev = new TicketPr("Backend-DotNet", "dev", 2, "completed", 0);
        var externalMain = new TicketPr("ExternalPortal-Angular", "main", 3, "active", 0);
        var abandoned = new TicketPr("Portal-Angular", "main", 4, "abandoned", 0);

        var post = TeamsPost.Build("MDP-1", "Summary", new[] { backendMain, backendDev, externalMain, abandoned });

        Assert.Equal(
            $"MDP-1 Summary\r\n\r\n\r\n\r\n[BE]\r\nmain: {backendMain.Url}\r\ndev: {backendDev.Url}\r\n\r\n[FE External]\r\nmain: {externalMain.Url}",
            post);
        Assert.DoesNotContain(abandoned.Url, post);
    }

    [Fact]
    public void Build_KeepsTheDescriptionLine_WhenNoPrAndNoSummary()
    {
        var post = TeamsPost.Build("MDP-1", null, Array.Empty<TicketPr>());

        Assert.Equal("MDP-1\r\n\r\n", post);
    }

    [Theory]
    [InlineData("Portal-Angular", "[FE Portal]")]
    [InlineData("MobileApp-Angular", "[FE Mobile]")]
    [InlineData("Foo-Angular", "[FE Foo]")]
    [InlineData("Other", "[Other]")]
    public void SectionHeader_NamesEachRepo(string repository, string expected)
    {
        var header = TeamsPost.SectionHeader(new TicketPr(repository, "main", 1, "active", 0));

        Assert.Equal(expected, header);
    }

    [Fact]
    public void ReadDraft_PrefersTheDevRepoDraft_ThenTheWorktree()
    {
        var worktreeOnlyDevRepo = Directory.CreateDirectory(Path.Combine(_root, "worktree-only", "dev")).FullName;
        var worktreeOnlyWorktree = WriteDraft(Path.Combine("worktree-only", "worktree"), "from worktree");

        Assert.Equal("from worktree", TeamsPost.ReadDraft(worktreeOnlyDevRepo, worktreeOnlyWorktree, "MDP-1"));

        var bothDevRepo = WriteDraft(Path.Combine("both", "dev"), "from dev repo");
        var bothWorktree = WriteDraft(Path.Combine("both", "worktree"), "from worktree");

        Assert.Equal("from dev repo", TeamsPost.ReadDraft(bothDevRepo, bothWorktree, "MDP-1"));
    }

    [Fact]
    public void ReadDraft_ReturnsNull_WhenTheDraftsAreBlankOrMissing()
    {
        var blankDevRepo = WriteDraft(Path.Combine("blank", "dev"), "  \r\n ");
        var blankWorktree = WriteDraft(Path.Combine("blank", "worktree"), "\n");
        var emptyDevRepo = Directory.CreateDirectory(Path.Combine(_root, "none", "dev")).FullName;
        var emptyWorktree = Directory.CreateDirectory(Path.Combine(_root, "none", "worktree")).FullName;

        Assert.Null(TeamsPost.ReadDraft(blankDevRepo, blankWorktree, "MDP-1"));
        Assert.Null(TeamsPost.ReadDraft(emptyDevRepo, emptyWorktree, "MDP-1"));
    }

    [Fact]
    public void ReadDraft_ReturnsCrLf_ForAnLfFile()
    {
        var devRepo = WriteDraft("lf", "Title\n\nmô tả\n");

        var draft = TeamsPost.ReadDraft(devRepo, Path.Combine(_root, "missing"), "MDP-1");

        Assert.Equal("Title\r\n\r\nmô tả", draft);
    }

    [Theory]
    [InlineData("T\r\n\r\nmô tả", 5)]
    [InlineData("T\r\n\r\n", 5)]
    [InlineData("T", 1)]
    [InlineData("T\r\nx", 4)]
    public void DescriptionStart_IsAfterTheSecondLineBreak(string text, int expected)
    {
        Assert.Equal(expected, TeamsPost.DescriptionStart(text));
    }

    [Fact]
    public void SplitSubject_TakesTheFirstLine_AndTrimsTheBody()
    {
        var (subject, body) = TeamsPost.SplitSubject("  MDP-1 Summary \r\n\r\nmô tả\r\n[BE]\r\nmain: x\r\n");

        Assert.Equal("MDP-1 Summary", subject);
        Assert.Equal("mô tả\n[BE]\nmain: x", body);
    }

    [Fact]
    public void SplitSubject_HasAnEmptyBody_WhenOnlyTheTitleIsWritten()
    {
        var (subject, body) = TeamsPost.SplitSubject("MDP-1 x\r\n\r\n ");

        Assert.Equal("MDP-1 x", subject);
        Assert.Equal("", body);
    }

    [Fact]
    public void ToHtml_KeepsVietnameseLetters()
    {
        Assert.Equal("Tiêu đề<br>mô tả", TeamsPost.ToHtml("Tiêu đề\r\nmô tả"));
    }

    [Fact]
    public void ToHtml_LeavesTrailingPunctuationOutOfTheLink_WhenAUrlEndsASentence()
    {
        var html = TeamsPost.ToHtml("xem https://a.b/c.\n(https://a.b/d), rồi");

        Assert.Equal("xem <a href=\"https://a.b/c\">https://a.b/c</a>.<br>(<a href=\"https://a.b/d\">https://a.b/d</a>), rồi", html);
    }

    [Fact]
    public void ToHtml_KeepsTheClosingParenthesis_WhenTheUrlOpensOne()
    {
        var html = TeamsPost.ToHtml("xem https://a.b/X_(Y).");

        Assert.Equal("xem <a href=\"https://a.b/X_(Y)\">https://a.b/X_(Y)</a>.", html);
    }

    [Fact]
    public void ToHtml_EscapesAndLinksUrls()
    {
        var html = TeamsPost.ToHtml("MDP-1 <x> & y\nmain: https://a.b/c?d=1&e=2\nsee <https://a.b/c>");

        Assert.Equal(
            "MDP-1 &lt;x&gt; &amp; y<br>main: <a href=\"https://a.b/c?d=1&amp;e=2\">https://a.b/c?d=1&amp;e=2</a><br>see &lt;<a href=\"https://a.b/c\">https://a.b/c</a>&gt;",
            html);
    }

    private static readonly TeamsPerson Lead = new("8:orgid:lead", "Minh Dinh (DU2 - Tech Leader)");
    private const string LeadGreeting = "a <span class=\"mention\" data-mri=\"8:orgid:lead\">Minh Dinh (DU2 - Tech Leader)</span>, dạ ";

    [Fact]
    public void WithGreeting_PrefixesDescription_AndLowercasesItsFirstLetter()
    {
        var greeted = TeamsPost.WithGreeting("MDP-1 x\r\n\r\nE update phần A\r\n\r\n[BE]\r\nmain: u", Lead);

        Assert.Equal($"MDP-1 x\r\n\r\n{LeadGreeting}e update phần A\r\n\r\n[BE]\r\nmain: u", greeted);
        Assert.Contains("\n" + LeadGreeting + "thêm", TeamsPost.WithGreeting("MDP-1 x\n\nThêm cột", Lead));
    }

    [Fact]
    public void WithGreeting_KeepsAcronymCase()
    {
        var greeted = TeamsPost.WithGreeting("MDP-1 x\n\nAPI trả về lỗi", Lead);

        Assert.Equal($"MDP-1 x\n\n{LeadGreeting}API trả về lỗi", greeted);
    }

    [Fact]
    public void WithGreeting_AddsLineBeforePrBlocks_WhenTemplateHasNoDescription()
    {
        var template = TeamsPost.Build("MDP-1", "x", new[] { new TicketPr("Backend-DotNet", "main", 1, "active", 0) });

        var greeted = TeamsPost.WithGreeting(template, null);

        Assert.Equal(template.Replace("[BE]", "dạ e update \r\n\r\n[BE]"), greeted);
        Assert.Equal("MDP-1\r\n\r\ndạ e update ", TeamsPost.WithGreeting("MDP-1\r\n\r\n", null));
    }

    [Theory]
    [InlineData("a Minh ơi, e update")]
    [InlineData("Dạ e update")]
    [InlineData("dạ, e update")]
    public void WithGreeting_LeavesDraft_WhenDescriptionAlreadyGreets(string description)
    {
        var draft = $"MDP-1 x\n\n{description}\n\n[BE]\nmain: u";

        Assert.Equal(draft, TeamsPost.WithGreeting(draft, Lead));
    }

    [Fact]
    public void WithGreeting_StillGreets_WhenFirstWordOnlyStartsWithDa()
    {
        var greeted = TeamsPost.WithGreeting("MDP-1 x\n\ndạng bảng mới", null);

        Assert.Equal("MDP-1 x\n\ndạ dạng bảng mới", greeted);
    }

    [Fact]
    public void WithGreeting_UsesOnlyDa_WhenNoLead()
    {
        var greeted = TeamsPost.WithGreeting("MDP-1 x\n\ne sửa lỗi", null);

        Assert.Equal("MDP-1 x\n\ndạ e sửa lỗi", greeted);
    }

    [Fact]
    public void WithGreeting_EscapesLeadNameAndMri()
    {
        var greeted = TeamsPost.WithGreeting("MDP-1 x\n\ne sửa", new TeamsPerson("8:a\"b", "A <b> & C"));

        Assert.Contains("<span class=\"mention\" data-mri=\"8:a&quot;b\">A &lt;b&gt; &amp; C</span>, dạ e sửa", greeted);
    }

    [Fact]
    public void ResolvePostLead_PrefersLearned_ThenMinhDinhFromPeople_ElseNull()
    {
        var minh = new TeamsPerson("8:minh", "minh dinh (DU2)");
        var people = new List<TeamsPerson> { new("8:other", "Ann"), minh, new("8:minh2", "Minh Dinh 2") };

        Assert.Equal(Lead, TeamsPost.ResolvePostLead(Lead, people));
        Assert.Equal(minh, TeamsPost.ResolvePostLead(null, people));
        Assert.Null(TeamsPost.ResolvePostLead(null, new List<TeamsPerson> { new("8:other", "Ann") }));
    }
}
