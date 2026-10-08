namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// An argument a tool refuses before it does anything is a sentence that did not parse — not work
/// that went wrong.
///
/// <para><b>What it cost.</b> 2026-09-23 00:15:44, run <c>8bddf1</c>:
/// <c>search_files {"pattern":"Interrupted","glob":"src/Enactive.Remote.*/*.cs"}</c>. The glob
/// names a path, which that argument does not take, and the tool said so clearly — and reported it
/// as a FAILURE. The model read the files another way and finished its work; step 2 was marked
/// INCOMPLETE for the refusal, and steps 3 and 4 were skipped.</para>
///
/// <para><b>Why a search is the worst place for this.</b> An open failure is closed by doing the
/// thing: writing the file it named, running the operation again, calling a tool of the same kind.
/// A search names no file, is not a shell and changes nothing — so a failure recorded against one
/// can be closed by NOTHING except repeating the identical bad call. It is a dead end by
/// construction, which is why the verdict has to be right at the tool.</para>
///
/// <para>The engine has held this since 2026-09-08 for <c>write_file</c> without a <c>path</c>:
/// <i>"not work that did not happen, it is a sentence that did not parse"</i>. These tools were
/// simply never told.</para>
/// </summary>
public sealed class AnArgumentTheToolRefusedTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts);

    /// <summary>The reported call, verbatim, and the two tools that take the same argument.</summary>
    [Theory]
    [InlineData("search_files")]
    [InlineData("count_matches")]
    [InlineData("file_stats")]
    public async Task A_glob_that_names_a_path_never_ran(string tool)
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "class Program { }");

        ITool instance = tool switch
        {
            "search_files"  => new SearchFilesTool(),
            "count_matches" => new CountMatchesTool(),
            _               => new FileStatsTool()
        };

        var args = tool == "file_stats"
            ? """{"glob":"src/Enactive.Remote.*/*.cs"}"""
            : """{"pattern":"Interrupted","glob":"src/Enactive.Remote.*/*.cs"}""";

        var result = await instance.InvokeAsync(args, Context(fx), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);

        // The explanation was already good. It is the verdict that was wrong, so it stays.
        Assert.Contains("FILE NAMES only", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the run survives it. The shape from the log: the argument is refused, the model says
    /// it properly, and the step is not left holding the first attempt. A search names no file and
    /// changes nothing, so the ONLY thing that can close it is another search - which is exactly
    /// what a model does one turn after being told what the argument takes.
    /// </summary>
    [Fact]
    public async Task A_step_that_said_it_properly_is_not_failed_for_the_first_try()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "class Program { }");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"look for it"}"""),
                    Turn.Calls1("search_files",
                                """{"pattern":"Interrupted","glob":"src/Enactive.Remote.*/*.cs"}"""),
                    Turn.Calls1("search_files",
                                """{"pattern":"Interrupted","glob":"*.cs"}""", "c2"),
                    Turn.Says("Nothing in the sources mentions it.")),
                EngineFixture.Role("developer")),
            "does anything mention Interrupted");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// THE BOUNDARY. A search that RAN and found nothing is an answer, and a path that is not there
    /// is a lookup told no — neither becomes "the call never happened". Those verdicts are older
    /// than this change and it must not smear them together: they say different things to the step.
    /// </summary>
    [Fact]
    public async Task A_search_that_actually_ran_is_not_called_unread()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "class Program { }");

        var ran = await new SearchFilesTool().InvokeAsync(
            """{"pattern":"zzznotthereatall","glob":"*.cs"}""", Context(fx), CancellationToken.None);

        Assert.True(ran.Success);
        Assert.False(ran.DidNotRun);

        var nowhere = await new SearchFilesTool().InvokeAsync(
            """{"pattern":"x","path":"no/such/folder"}""", Context(fx), CancellationToken.None);

        Assert.False(nowhere.Success);
        Assert.False(nowhere.DidNotRun, nowhere.Error);
        Assert.True(nowhere.IsAnswer, nowhere.Error);
    }
}
