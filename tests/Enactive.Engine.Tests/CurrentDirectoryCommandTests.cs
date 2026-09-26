namespace Enactive.Engine.Tests;

using Enactive.Tools;

[Collection("processes")]
public sealed class CurrentDirectoryCommandTests
{
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_workspace_script_path_works_without_changing_lookup_policy(bool protectedLookup)
    {
        const string variable = "NoDefaultCurrentDirectoryInExePath";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, protectedLookup ? "1" : null);
            using var fx = new EngineFixture();
            fx.Write("unique-workspace-script.cmd", "@echo off\r\necho WORKSPACE_SCRIPT_OK\r\n");
            var explicitPath = await fx.Invoke(new RunCommandTool(),
                """{"command":".\\unique-workspace-script.cmd"}""");
            Assert.True(explicitPath.Success, explicitPath.Error);
            Assert.Contains("WORKSPACE_SCRIPT_OK", explicitPath.Output);
            var bare = await fx.Invoke(new RunCommandTool(), """{"command":"unique-workspace-script.cmd"}""");
            if (protectedLookup)
            {
                Assert.False(bare.Success);
                Assert.True(bare.DidNotRun);
                Assert.Contains(@".\script.cmd", bare.Error);
                Assert.Equal("1", Environment.GetEnvironmentVariable(variable));
            }
            else Assert.True(bare.Success, bare.Error);
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }
}
