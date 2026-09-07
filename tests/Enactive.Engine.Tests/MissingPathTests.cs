namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 19:20: "что опять?"
///
/// <para>A documentation-sync run. Step 1 listed <c>tests/ParserSmokeTest</c>, read the
/// <c>Program.cs</c> it found there, guessed that there would also be a
/// <c>ParserSmokeTest.cs</c> beside it, was told there is not, and carried on and did the
/// work. The run was failed anyway:</para>
///
/// <para><c>Finished without resolving 1 tool call(s) that did not go through: read_file
/// {"path":"tests/ParserSmokeTest/ParserSmokeTest.cs"} — File not found</c></para>
///
/// <para>Step 1 INCOMPLETE, step 2 skipped behind it, run failed. Nothing was wrong with the
/// work.</para>
///
/// <para><b>A lookup that finds nothing has ANSWERED.</b> "There is no such file" is the
/// information the model asked for; there is nothing unfinished and nothing to retry, and
/// guessing at a name and being told no is how anything explores a tree it has not seen. The
/// guard that catches a step which never read the file it was told to read must not also catch
/// a step which asked a question and got an answer.</para>
///
/// <para>The distinction cannot be made from the error text — <c>edit_file</c> says "File not
/// found" too, and there it means an edit that did not happen. It is made by the TOOL, which is
/// the only thing that knows whether it was asked a question or told to do something:
/// <see cref="ToolResults.NotFound"/> for a lookup, <see cref="ToolResults.Fail"/> for the
/// rest.</para>
/// </summary>
public sealed class MissingPathTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"do the thing"}""";

    private const string TwoStepPlan = """
        {"disposition":"task","title":"sync the docs",
         "steps":[{"title":"read the code","dependsOn":[]},{"title":"update README.md","dependsOn":[0]}]}
        """;

    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts, Services: null!);

    private static Task<ToolResult> Call(ITool tool, EngineFixture fx, string argumentsJson)
        => tool.InvokeAsync(argumentsJson, Context(fx), CancellationToken.None);

    private static string Problems(IEnumerable<WorkEvent> events)
        => string.Join("\n", events.OfKind(EventKind.ErrorObserved).Select(e => e.Summary));

    // ── the reported run ────────────────────────────────────────────────────

    /// <summary>
    /// The one that matters, to the shape of the log: a step that guessed at a filename, was told
    /// no, and did the work is a step that finished — and the step waiting on it runs.
    /// </summary>
    [Fact]
    public async Task A_guess_at_a_filename_does_not_fail_the_step_that_did_the_work()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "// the one file that is really there");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(TwoStepPlan),
                    // Step 1: read what exists, then guess at what does not.
                    Turn.Calls1("read_file", """{"path":"Program.cs"}"""),
                    Turn.Calls1("read_file", """{"path":"ParserSmokeTest.cs"}""", "call_2"),
                    Turn.Says("There is no ParserSmokeTest.cs; Program.cs is the whole of it."),
                    // Step 2: the work the run was actually for.
                    Turn.Calls1("write_file", """{"path":"README.md","content":"# Parser\n"}""", "call_3"),
                    Turn.Says("Updated README.md.")),
                EngineFixture.Role("developer")),
            "bring README.md into line with the code");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.DoesNotContain("Finished without resolving", Problems(events), StringComparison.Ordinal);
        Assert.Equal("# Parser\n", fx.Read("README.md"));
    }

    /// <summary>
    /// And nothing downstream is skipped. This is what the person actually lost: the second step
    /// never ran, so the documentation the run existed to write was not written.
    /// </summary>
    [Fact]
    public async Task The_step_waiting_behind_it_is_not_skipped()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(TwoStepPlan),
                    Turn.Calls1("list_dir", """{"path":"."}"""),
                    Turn.Calls1("list_dir", """{"path":"tests/ParserSmokeTest"}""", "call_2"),
                    Turn.Says("There is no tests folder."),
                    Turn.Calls1("write_file", """{"path":"README.md","content":"nothing to document"}""", "call_3"),
                    Turn.Says("Updated README.md.")),
                EngineFixture.Role("developer")),
            "sync the docs");

        Assert.DoesNotContain(events, e => e.Summary.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        Assert.True(fx.Exists("README.md"), events.Text());
    }

    /// <summary>
    /// The condition on the forgiveness, and the reason two shipping tests refused the first cut
    /// of this fix: a step whose ENTIRE record is lookups that found nothing has produced nothing,
    /// and "Done" over that is what the guard was built for. One call that worked is the whole
    /// difference between a step exploring and a step with nothing to show.
    ///
    /// <para>This is <c>FollowupReviewTests.A_tool_failure_the_model_talked_past_does_not_complete</c>
    /// stated from the other side: the model was told to read a file, could not, and said "Done".</para>
    /// </summary>
    [Fact]
    public async Task A_step_that_found_nothing_and_did_nothing_has_not_finished()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("read_file", """{"path":"required-input.txt"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "read required-input.txt and summarise it");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
        Assert.Contains("nothing", Problems(events), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And that message says which of the two things went wrong. "Did not go through" sends somebody
    /// to look for a broken tool; these calls went through perfectly and answered no.
    /// </summary>
    [Fact]
    public async Task A_step_that_found_nothing_is_not_described_as_a_call_that_broke()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("read_file", """{"path":"required-input.txt"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "read required-input.txt");

        Assert.DoesNotContain("did not go through", Problems(events), StringComparison.Ordinal);
    }

    /// <summary>One call that worked is enough. That is the line, stated on its own.</summary>
    [Fact]
    public async Task One_call_that_worked_is_enough_to_make_a_miss_exploration()
    {
        using var fx = new EngineFixture();
        fx.Write("here.cs", "// something real");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("read_file", """{"path":"here.cs"}"""),
                    Turn.Calls1("read_file", """{"path":"guessed.cs"}""", "call_2"),
                    Turn.Says("Only here.cs exists.")),
                EngineFixture.Role("developer")),
            "look at the sources");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// The failure is still REPORTED. A read that found nothing is not promoted to a success:
    /// the model is told, the event says failed, and the reviewer's evidence carries it. Only the
    /// "this step is unfinished" judgement changes.
    /// </summary>
    [Fact]
    public async Task The_read_is_still_reported_as_having_failed()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("read_file", """{"path":"nowhere.cs"}"""),
                    Turn.Says("There is no such file.")),
                EngineFixture.Role("developer")),
            "look at nowhere.cs");

        Assert.Contains(
            events.OfKind(EventKind.ToolResult),
            e => e.Summary.Contains("read_file -> failed", StringComparison.Ordinal)
                 && e.Summary.Contains("File not found", StringComparison.Ordinal));
    }

    // ── what must NOT change ────────────────────────────────────────────────

    /// <summary>
    /// The same six words, the opposite meaning. <c>edit_file</c> reports "File not found" when the
    /// file it was told to change is not there — that edit did not happen, and the step is
    /// unfinished exactly as before. This is why the error text could never have been the test.
    /// </summary>
    [Fact]
    public async Task An_edit_that_could_not_find_its_file_still_holds_the_step_open()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("edit_file", """{"path":"gone.cs","old_string":"a","new_string":"b"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "rename a to b in gone.cs");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
        Assert.Contains("Finished without resolving", Problems(events), StringComparison.Ordinal);
    }

    /// <summary>
    /// A write that could not be made is still an unfinished step — the original defect, which this
    /// narrowing must leave exactly where it was.
    /// </summary>
    [Fact]
    public async Task A_write_that_failed_still_holds_the_step_open()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    // No 'content': the arguments are not what the model meant to send.
                    Turn.Calls1("write_file", """{"path":"notes.md"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "write notes.md");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
        Assert.Contains("Finished without resolving", Problems(events), StringComparison.Ordinal);
    }

    // ── which tools may say it ──────────────────────────────────────────────

    /// <summary>A lookup that found nothing: the failure IS the answer.</summary>
    [Fact]
    public async Task A_read_of_a_missing_file_answers()
    {
        using var fx = new EngineFixture();

        var result = await Call(new ReadFileTool(), fx, """{"path":"nowhere.cs"}""");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
        Assert.Contains("File not found", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listing_of_a_missing_folder_answers()
    {
        using var fx = new EngineFixture();

        var result = await Call(new ListDirectoryTool(), fx, """{"path":"tests/ParserSmokeTest"}""");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
    }

    [Fact]
    public async Task A_search_under_a_missing_folder_answers()
    {
        using var fx = new EngineFixture();

        var result = await Call(new SearchFilesTool(), fx, """{"pattern":"anything","path":"no/such/place"}""");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
    }

    /// <summary>
    /// An action that could not find its target does NOT answer — the same words from a different
    /// tool mean a different thing, and this is the pair that proves the fix is not a string match.
    /// </summary>
    [Fact]
    public async Task An_edit_of_a_missing_file_does_not_answer()
    {
        using var fx = new EngineFixture();

        var result = await Call(new EditFileTool(), fx,
            """{"path":"gone.cs","old_string":"a","new_string":"b"}""");

        Assert.False(result.Success);
        Assert.Contains("File not found", result.Error!, StringComparison.Ordinal);
        Assert.False(result.IsAnswer, "an edit that did not happen is not an answer");
    }

    [Fact]
    public async Task A_move_of_a_missing_file_does_not_answer()
    {
        using var fx = new EngineFixture();

        var result = await Call(new MoveFileTool(), fx, """{"from":"gone.cs","to":"here.cs"}""");

        Assert.False(result.Success);
        Assert.False(result.IsAnswer, "a move that did not happen is not an answer");
    }

    /// <summary>A read that failed for any OTHER reason is a failure like any other.</summary>
    [Fact]
    public async Task A_read_that_escapes_the_workspace_does_not_answer()
    {
        using var fx = new EngineFixture();

        var result = await Call(new ReadFileTool(), fx, """{"path":"../../secrets.txt"}""");

        Assert.False(result.Success);
        Assert.False(result.IsAnswer, "a refused path is not a missing one");
    }

    /// <summary>And a successful call is never one of these — the flag is only about failures.</summary>
    [Fact]
    public async Task A_call_that_worked_is_not_an_answer_in_this_sense()
    {
        using var fx = new EngineFixture();
        fx.Write("here.cs", "// content");

        var result = await Call(new ReadFileTool(), fx, """{"path":"here.cs"}""");

        Assert.True(result.Success, result.Error);
        Assert.False(result.IsAnswer);
    }

    /// <summary>
    /// The factory itself, so the meaning survives a refactor: <see cref="ToolResults.Fail"/> is
    /// the default and <see cref="ToolResults.NotFound"/> is the exception a tool has to choose.
    /// </summary>
    [Fact]
    public void The_default_for_a_failure_is_still_unfinished()
    {
        Assert.False(ToolResults.Fail("something broke").IsAnswer);
        Assert.True(ToolResults.NotFound("File not found: x").IsAnswer);
        Assert.False(ToolResults.NotFound("File not found: x").Success);
        Assert.False(ToolResults.Ok("fine").IsAnswer);
    }
}
