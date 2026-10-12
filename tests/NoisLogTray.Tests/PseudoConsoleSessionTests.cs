using System.Collections;

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

        Assert.Equal("APPDATA=C:\\Users\\me\\AppData\\Roaming\0PATH=C:\\bin\0\0", block);
    }
}
