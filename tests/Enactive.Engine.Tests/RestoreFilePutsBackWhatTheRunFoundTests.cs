namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Run bb77e810, 2026-10-09: asked to break code on purpose and put it back, a worker had no way to put it back but
/// by hand - and a step titled "Restore behaviour" rewrote the code into a version the request never had. restore_file
/// puts a file back byte for byte from the copy the engine kept of it before the run.
/// </summary>
public sealed class RestoreFilePutsBackWhatTheRunFoundTests
{
    private static string Path(string path) => JsonSerializer.Serialize(new { path });

    [Fact]
    public async Task A_file_the_run_changed_is_put_back_exactly_as_it_was()
    {
        using var fx = new EngineFixture();
        fx.Write("Stats.cs", "int Median() => sorted[mid];\r\n");
        await fx.Invoke(new EditFileTool(), """{"path":"Stats.cs","old_string":"sorted[mid]","new_string":"0"}""");

        var restored = await fx.Invoke(new RestoreFileTool(), Path("Stats.cs"));

        Assert.True(restored.Success, restored.Error);
        Assert.Equal("int Median() => sorted[mid];\r\n", fx.Read("Stats.cs"));
        var again = await fx.Invoke(new RestoreFileTool(), Path("Stats.cs"));
        Assert.Contains("already exactly as it was", again.Output, StringComparison.Ordinal);
    }

    /// <summary>A first change that only appended displaced nothing: what was there before the append is how it was.</summary>
    [Fact]
    public async Task A_file_the_run_appended_to_is_put_back_without_the_append()
    {
        using var fx = new EngineFixture();
        fx.Write("log.txt", "line one\n");
        await fx.Invoke(new WriteFileTool(), """{"path":"log.txt","content":"line two\n","append":true}""");

        await fx.Invoke(new RestoreFileTool(), Path("log.txt"));

        Assert.Equal("line one\n", fx.Read("log.txt"));
    }

    /// <summary>A file the run made had nothing before it; taking it away is delete_file's, which asks first.</summary>
    [Fact]
    public async Task A_file_the_run_made_is_not_taken_away()
    {
        using var fx = new EngineFixture();
        await fx.Invoke(new WriteFileTool(), """{"path":"new.txt","content":"made here"}""");

        var restored = await fx.Invoke(new RestoreFileTool(), Path("new.txt"));

        Assert.False(restored.Success);
        Assert.Contains("made by this run", restored.Error, StringComparison.Ordinal);
        Assert.Equal("made here", fx.Read("new.txt"));
    }

    [Fact]
    public async Task A_file_the_run_has_not_changed_is_left_as_it_is_and_said()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");

        var restored = await fx.Invoke(new RestoreFileTool(), Path("notes.txt"));

        Assert.True(restored.Success);
        Assert.Contains("nothing was put back", restored.Output, StringComparison.Ordinal);
        Assert.Equal(WorkspaceEffect.None, restored.WorkspaceEffect);
    }

    /// <summary>Putting back what the run found cannot go against a limit on changing it: it is not asked about.</summary>
    [Fact]
    public async Task Putting_a_file_back_is_not_asked_about_against_a_change_limit()
    {
        using var fx = new EngineFixture();
        fx.Write("Stats.cs", "code");
        var planner = new FakeChatProvider();
        var guard = new ChangeLimitGuard("Leave Stats.cs alone.", ["Leave Stats.cs alone."], planner, new ModelRef("p", "m"),
            new RunBudget(null, DateTimeOffset.UtcNow), 1000);

        var decision = await guard.CheckAsync(1, "Put it back", null, new ToolCall("c1", "restore_file", Path("Stats.cs")),
            new RestoreFileTool().Definition, new DiskArtifactStore(fx.Workspace).BeginStep(), fx.Workspace.RootPath, default);

        Assert.Null(decision.Refusal);
        Assert.Empty(planner.Requests);
    }

    [Theory]
    [InlineData("developer")]
    [InlineData("writer")]
    public void The_roles_that_write_files_have_it(string role)
        => Assert.Contains("restore_file", DefaultWorkers.Build(new ModelRef("test", "model")).Single(w => w.Id == role).ToolAllowlist);
}
