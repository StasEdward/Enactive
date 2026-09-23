namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// <c>read_files</c> — several files in one turn.
///
/// <para><b>The measurement that asked for it.</b> 2026-09-24, the wiki audit that finished: 270
/// tool calls, of which <b>128 were <c>read_file</c></b> and 119 were searches. A page's claims
/// point at four or five source files, and each was a round trip through an eight-thousand-token
/// transcript. A run costs what it re-sends, so turns are the unit worth cutting.</para>
///
/// <para><b>Why not the MCP server that already has one.</b> <c>desktop-commander</c> offers
/// <c>read_multiple_files</c>; offered alongside our 16 tools on 2026-09-23, its 27 were called
/// zero times. Adoption is not the deciding reason though — a tool outside this registry returns
/// text and no artifact, so what it touches is invisible to the reviewer's file list, to staging
/// and revert, and to the write journal. Harmless for a reader, which is why one reader was worth
/// writing and the server was not.</para>
/// </summary>
public sealed class ReadingFiveFilesInOneTurnTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts, Services: null!);

    private static Task<ToolResult> Read(EngineFixture fx, params string[] paths)
        => new ReadFilesTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new { paths }),
            Context(fx), CancellationToken.None);

    [Fact]
    public async Task Five_files_come_back_in_one_answer()
    {
        using var fx = new EngineFixture();
        for (var i = 1; i <= 5; i++)
            fx.Write($"f{i}.md", $"contents of file {i}");

        var result = await Read(fx, "f1.md", "f2.md", "f3.md", "f4.md", "f5.md");

        Assert.True(result.Success, result.Error);
        for (var i = 1; i <= 5; i++)
        {
            Assert.Contains($"f{i}.md", result.Output, StringComparison.Ordinal);
            Assert.Contains($"contents of file {i}", result.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A file that is not there is an ANSWER, and it is NAMED. A model that asked for five and got
    /// four back would otherwise have to work out which one it never saw.
    /// </summary>
    [Fact]
    public async Task A_missing_one_is_named_and_the_rest_still_arrive()
    {
        using var fx = new EngineFixture();
        fx.Write("here.md", "present");

        var result = await Read(fx, "here.md", "gone.md");

        Assert.True(result.Success, result.Error);
        Assert.Contains("present", result.Output, StringComparison.Ordinal);
        Assert.Contains("not there: gone.md", result.Output, StringComparison.Ordinal);
    }

    /// <summary>All of them absent is the same as one absent for read_file: a lookup told no.</summary>
    [Fact]
    public async Task None_of_them_there_is_an_answer_not_a_failure()
    {
        using var fx = new EngineFixture();

        var result = await Read(fx, "a.md", "b.md");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
        Assert.False(result.DidNotRun, result.Error);
        Assert.Contains("a.md", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// One path that leaves the workspace makes the WHOLE call wrong, and nothing is read. A
    /// partial answer over a refused argument is worse than none: the model would read four files
    /// and never learn the fifth was refused rather than empty.
    /// </summary>
    [Fact]
    public async Task One_path_outside_the_workspace_refuses_the_whole_call()
    {
        using var fx = new EngineFixture();
        fx.Write("here.md", "present");

        var result = await Read(fx, "here.md", "../../secrets.txt");

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);
        Assert.DoesNotContain("present", result.Output ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"paths":"a.md"}""")]     // a string where an array belongs
    [InlineData("""{"paths":[]}""")]
    [InlineData("""{"path":"a.md"}""")]      // read_file's argument, sent to this one
    public async Task Arguments_it_cannot_read_never_ran(string args)
    {
        using var fx = new EngineFixture();

        var result = await new ReadFilesTool().InvokeAsync(args, Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);
    }

    /// <summary>
    /// And it reaches a run. A tool the host registers and no role names may as well not exist —
    /// this file's own registry has learnt that six times (§9bt).
    /// </summary>
    [Fact]
    public void Every_role_that_can_read_one_file_can_read_several()
    {
        foreach (var worker in Enactive.Agents.DefaultWorkers.Seed(
                     new Enactive.Core.Providers.ModelRef("fake", "fake-model")))
            if (worker.ToolAllowlist.Contains("read_file"))
                Assert.Contains("read_files", worker.ToolAllowlist);
    }
}
