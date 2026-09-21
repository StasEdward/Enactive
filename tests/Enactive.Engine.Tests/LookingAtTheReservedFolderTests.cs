namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Tools;
using Xunit;

/// <summary>
/// What the tools say when a model looks at <c>.enactive</c>.
///
/// <para><b>Measured twice on 2026-09-21</b>, in two live runs of two different requests. The
/// worker is told it has a working area at <c>.enactive/scratch/</c>, so it looks at
/// <c>.enactive</c> to find it — and <c>list_dir</c> answered <i>"'.enactive' holds the
/// workspace's own state and is not writable by tools"</i>, about a call that was only looking.
/// Worse than the wrong word: the refusal came back as a failed call, so it held the step open as
/// unfinished work. In one of the two runs it was one of the two unresolved calls that failed
/// it.</para>
///
/// <para>The third of this shape in two days, after <c>list_dir</c> on a file and
/// <c>search_files</c> on a file: a message that describes a problem the caller does not have.
/// The rule itself is not in question — a tool has no business in the undo journal — only what is
/// said about it, and what kind of result it is.</para>
/// </summary>
public sealed class LookingAtTheReservedFolderTests
{
    private static string Path(string p)
        => $$"""{"path": {{JsonSerializer.Serialize(p)}} }""";

    /// <summary>
    /// A read refused here has ANSWERED: the model asked whether it could look and was told no,
    /// definitively. Nothing is half-done and there is nothing to retry, so it must not hold the
    /// step open — the same rule a lookup that finds nothing already gets.
    /// </summary>
    [Theory]
    [InlineData(".enactive")]
    [InlineData(".enactive/runs")]
    public async Task Looking_into_the_reserved_folder_is_answered_not_failed(string path)
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new ListDirectoryTool(), Path(path));

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, "a refusal to look is an answer, not unfinished work");
    }

    /// <summary>And it says the true thing, including where the model's own area actually is.</summary>
    [Fact]
    public async Task It_names_the_working_area_instead_of_talking_about_writing()
    {
        using var fx = new EngineFixture();

        var text = (await fx.Invoke(new ListDirectoryTool(), Path(".enactive"))).Error ?? "";

        Assert.Contains(WorkspaceGuard.ScratchPrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("not writable", text, StringComparison.OrdinalIgnoreCase);

        // The parameter name belongs in a stack trace, not in an instruction to a model.
        Assert.DoesNotContain("Parameter", text, StringComparison.Ordinal);
    }

    /// <summary>read_file and search_files are the same question and get the same answer.</summary>
    [Fact]
    public async Task The_other_read_tools_answer_the_same_way()
    {
        using var fx = new EngineFixture();

        var read = await fx.Invoke(new ReadFileTool(), Path(".enactive/state.json"));
        var search = await fx.Invoke(
            new SearchFilesTool(), """{"pattern":"x","path":".enactive"}""");

        Assert.True(read.IsAnswer, "read_file");
        Assert.True(search.IsAnswer, "search_files");
    }

    /// <summary>
    /// The carve-out still works, and is the whole reason a model looks in here: its own working
    /// area is inside the reserved folder and is ordinary.
    /// </summary>
    [Fact]
    public async Task The_working_area_itself_is_listed_normally()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/scratch/probe.txt", "hello");

        var result = await fx.Invoke(new ListDirectoryTool(), Path(WorkspaceGuard.ScratchPrefix));

        Assert.True(result.Success, result.Error);
        Assert.Contains("probe.txt", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A WRITE refused here is still work that did not happen, and still holds the step. That is
    /// the whole reason this is one exception type read and write can answer differently.
    /// </summary>
    [Fact]
    public async Task Writing_into_the_reserved_folder_is_still_a_failure()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(
            new WriteFileTool(), """{"path":".enactive/runs/forged.json","content":"{}"}""");

        Assert.False(result.Success);
        Assert.False(result.IsAnswer, "a write that did not happen is unfinished work");
    }
}
