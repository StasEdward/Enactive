namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Asking about ONE named file is a question, not a mistake — in all three tools that take a path.
///
/// <para><c>search_files</c> learnt this on 2026-09-20: a run asked for a pattern in
/// <c>build_output.txt</c> and was told <i>"Not a folder in this workspace"</i> about a file
/// sitting in the workspace root that it had listed a moment earlier, then spent a turn on a
/// <c>for %f in (…) do</c> loop to get what it had asked for. "Answering is better than explaining,
/// and searching one named file is a perfectly good question."</para>
///
/// <para><c>count_matches</c> and <c>file_stats</c> were written after that and inherited the older
/// behaviour anyway. Measured 2026-09-23 21:13:45, in the run that finished:
/// <c>file_stats {"path":"Docs/DRIFT_ollama.md"}</c> — on the report the step had just written —
/// answered "Not a folder in this workspace" about a file that was there. It cost nothing, because
/// a miss is an answer, but it is a wrong sentence about a right path.</para>
/// </summary>
public sealed class OneNamedFileIsAQuestionTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts);

    [Fact]
    public async Task File_stats_of_one_named_file_answers()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", "one\ntwo\nthree\n");

        var result = await new FileStatsTool().InvokeAsync(
            """{"path":"report.md"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("report.md", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Count_matches_in_one_named_file_answers()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", "alpha\nbeta\nalpha\n");

        var result = await new CountMatchesTool().InvokeAsync(
            """{"path":"report.md","pattern":"alpha"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("2", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Naming the file IS the filter. A glob that would not have matched it does not get to
    /// exclude it — the same rule search_files applies, for the same reason.
    /// </summary>
    [Fact]
    public async Task A_glob_does_not_exclude_the_file_that_was_named()
    {
        using var fx = new EngineFixture();
        fx.Write("report.md", "alpha\n");

        var result = await new CountMatchesTool().InvokeAsync(
            """{"path":"report.md","pattern":"alpha","glob":"*.cs"}""",
            Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("1", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOUNDARY. A path that is neither is still nothing found — and the sentence now says so
    /// about both kinds, instead of asking for a folder.
    /// </summary>
    [Theory]
    [InlineData("file_stats")]
    [InlineData("count_matches")]
    public async Task A_path_that_is_neither_says_so(string tool)
    {
        using var fx = new EngineFixture();

        ITool instance = tool == "file_stats" ? new FileStatsTool() : new CountMatchesTool();
        var args = tool == "file_stats"
            ? """{"path":"no/such/place"}"""
            : """{"path":"no/such/place","pattern":"x"}""";

        var result = await instance.InvokeAsync(args, Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
        Assert.Contains("or file", result.Error, StringComparison.Ordinal);
    }
}
