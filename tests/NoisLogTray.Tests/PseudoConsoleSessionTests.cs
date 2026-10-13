using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

namespace NoisLogTray.Tests;

public class PseudoConsoleSessionTests
{
    [Fact]
    public void BuildEnvironmentBlock_DropsClaudeSessionVariables_AndEndsWithAnEmptyEntry()
    {
        var variables = new Hashtable
        {
            ["PATH"] = @"C:\bin",
            ["CLAUDECODE"] = "1",
            ["ANTHROPIC_MODEL"] = "x",
            ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
        };

        var block = PseudoConsoleSession.BuildEnvironmentBlock(variables);

        Assert.Equal("APPDATA=C:\\Users\\me\\AppData\\Roaming\0CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN=1\0PATH=C:\\bin\0\0", block);
    }

    [Fact]
    public void BundledConpty_IsCopiedBesideTheApp()
    {
        Assert.True(PseudoConsoleSession.UsesBundledConpty);
    }

    [Fact]
    public void Session_ShowsACommandsOutput()
    {
        var output = new StringBuilder();
        // /k keeps cmd at its prompt, so typed input reaches it through the console too.
        using var session = new PseudoConsoleSession("cmd.exe /k echo conpty-ok", Environment.CurrentDirectory, 80, 25);
        session.Output += bytes =>
        {
            lock (output) output.Append(Encoding.UTF8.GetString(bytes));
        };

        WaitForScreenText(output, "conpty-ok");
        session.Write(Encoding.ASCII.GetBytes("echo typed-ok\r"));
        WaitForScreenText(output, "typed-ok");

        Assert.Contains("conpty-ok", ScreenText(output));
        Assert.Contains("typed-ok", ScreenText(output));
    }

    [Fact]
    public void Session_RaisesExited_WhenTheCommandQuits()
    {
        var output = new StringBuilder();
        using var exited = new ManualResetEventSlim();
        using var session = new PseudoConsoleSession("cmd.exe /k echo ready", Environment.CurrentDirectory, 80, 25);
        session.Output += bytes =>
        {
            lock (output) output.Append(Encoding.UTF8.GetString(bytes));
        };
        session.Exited += exited.Set;
        WaitForScreenText(output, "ready");

        // What typing exit (or claude's /exit) does: the process ends, while ConPTY keeps its output pipe open.
        session.Write(Encoding.ASCII.GetBytes("exit\r"));

        Assert.True(exited.Wait(TimeSpan.FromSeconds(10)), "Exited was not raised after the command quit");
    }

    private static void WaitForScreenText(StringBuilder output, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !ScreenText(output).Contains(text)) Thread.Sleep(50);
    }

    // The output minus the window-title sequences, which carry the command line and would match on their own.
    private static string ScreenText(StringBuilder output)
    {
        lock (output) return Regex.Replace(output.ToString(), "\x1b\\][^\x07\x1b]*(\x07|\x1b\\\\)", "");
    }

    [Fact]
    public void BuildEnvironmentBlock_ForcesTheClassicRenderer_OverAnInheritedValue()
    {
        var variables = new Hashtable { ["CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN"] = "0" };

        var block = PseudoConsoleSession.BuildEnvironmentBlock(variables);

        Assert.Equal("CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN=1\0\0", block);
    }
}
