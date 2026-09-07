namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 21:01. Step 2, "Implement failing tests for identified gaps",
/// was marked Incomplete for three unresolved calls. It had written the tests.
///
/// <list type="number">
/// <item><c>edit_file</c> on Program.cs — <i>'old_string' does not appear</i>. So the model rewrote
/// the whole file with <c>write_file</c>, which SUCCEEDED: <i>"REPLACED the existing file
/// 'tests/ParserSmokeTest/Program.cs' (12251 bytes)"</i>.</item>
/// <item><c>write_file</c> on Program.cs — <i>'path' is required</i>, malformed arguments. The model
/// re-sent it correctly twenty-two seconds later, and that one succeeded.</item>
/// <item><c>run_command</c> — exit 1, the test runner reporting five failures, which the model then
/// reported accurately including that older tests had regressed.</item>
/// </list>
///
/// <para>Two of those three were made good by the model itself, on the same file, in the same step.
/// The tests exist on disk. Step 3 was skipped anyway and the run failed.</para>
///
/// <para><b>The rule took a tool call for the goal.</b> "Recovered only when the SAME call — same
/// tool, same arguments — later succeeds" was written knowing the cost, and called it "the honest
/// reading: what it was asked to do did not happen". This log is where that stops being true.
/// Nobody asked for <c>edit_file</c> with that particular <c>old_string</c>; the model chose it, the
/// choice did not match, and it rewrote the file instead. That is a model correcting itself, which
/// is the behaviour this engine spends most of its prompts trying to produce.</para>
///
/// <para>The FILE is what was asked for. So a failure that named a file is closed when a later call
/// in the same step produces that file — read from the artifacts the tool actually returned, not
/// inferred from what the model said. Everything else is unchanged: a failed command still has no
/// file to close it, and a failure nothing recovered still ends the step.</para>
/// </summary>
public sealed class RecoveredByAnotherRouteTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the tests"}""";

    // ── the two the model made good ─────────────────────────────────────────

    /// <summary>
    /// An edit that did not match, then a write of the whole file. To scale: this is exactly what
    /// the reported step did, and it is the recommended fallback when edit_file cannot match.
    /// </summary>
    [Fact]
    public async Task An_edit_that_did_not_match_is_closed_by_rewriting_the_file()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "// the original\n");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("edit_file",
                                """{"path":"Program.cs","old_string":"nowhere","new_string":"x"}"""),
                    Turn.Calls1("write_file",
                                """{"path":"Program.cs","content":"// the tests\n"}""", "c2"),
                    Turn.Says("Rewrote Program.cs; the edit would not match.")),
                EngineFixture.Role("developer")),
            "add the tests");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.Equal("// the tests\n", fx.Read("Program.cs"));
    }

    /// <summary>
    /// A call sent without its path, then the same call sent properly. A model fixing its own
    /// malformed arguments is the most ordinary recovery there is.
    /// </summary>
    [Fact]
    public async Task Malformed_arguments_are_closed_by_sending_them_properly()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("write_file", """{"content":"// the tests\n"}"""),
                    Turn.Calls1("write_file",
                                """{"path":"Program.cs","content":"// the tests\n"}""", "c2"),
                    Turn.Says("Wrote Program.cs.")),
                EngineFixture.Role("developer")),
            "add the tests");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.True(fx.Exists("Program.cs"));
    }

    /// <summary>Both at once, which is the reported step.</summary>
    [Fact]
    public async Task The_reported_step_finishes()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "// the original\n");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("edit_file",
                                """{"path":"Program.cs","old_string":"nowhere","new_string":"x"}"""),
                    Turn.Calls1("write_file", """{"content":"// no path\n"}""", "c2"),
                    Turn.Calls1("write_file",
                                """{"path":"Program.cs","content":"// the tests\n"}""", "c3"),
                    Turn.Says("Implemented the tests in Program.cs.")),
                EngineFixture.Role("developer")),
            "implement the gap tests");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>A move closes a failure on the file it creates, not the one it empties.</summary>
    [Fact]
    public async Task A_move_closes_a_failure_on_the_file_it_creates()
    {
        using var fx = new EngineFixture();
        fx.Write("old.cs", "// content\n");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("write_file", """{"path":"new.cs"}"""),
                    Turn.Calls1("move_file", """{"from":"old.cs","to":"new.cs"}""", "c2"),
                    Turn.Says("Moved it instead.")),
                EngineFixture.Role("developer")),
            "produce new.cs");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    // ── what must NOT change ────────────────────────────────────────────────

    /// <summary>
    /// The founding case. A failed call that nothing recovered still ends the step — writing some
    /// OTHER file is not recovery, and a step allowed to pass on that would be back where the guard
    /// started.
    /// </summary>
    [Fact]
    public async Task Writing_a_different_file_does_not_close_the_failure()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "// the original\n");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("edit_file",
                                """{"path":"Program.cs","old_string":"nowhere","new_string":"x"}"""),
                    Turn.Calls1("write_file", """{"path":"Notes.md","content":"# notes\n"}""", "c2"),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "edit Program.cs");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
        Assert.Equal("// the original\n", fx.Read("Program.cs"));
    }

    /// <summary>
    /// A failed COMMAND has no file to close it. Nothing here rescues a build that did not build:
    /// this is about files, and a command is judged as it was before.
    /// </summary>
    [Fact]
    public async Task A_failed_command_is_not_closed_by_writing_a_file()
    {
        using var fx = new EngineFixture();
        var fails = OperatingSystem.IsWindows() ? "exit /b 1" : "exit 1";

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("run_command", $$"""{"command":"{{fails}}"}"""),
                    Turn.Calls1("write_file", """{"path":"report.md","content":"# it failed\n"}""", "c2"),
                    Turn.Says("Wrote up the failure.")),
                EngineFixture.Role("developer")),
            "build and report");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// And a step that only failed still fails. The narrowing is about recovery, not about letting
    /// a failure through when nothing came after it.
    /// </summary>
    [Fact]
    public async Task A_failure_with_nothing_after_it_still_ends_the_step()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "// the original\n");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("edit_file",
                                """{"path":"Program.cs","old_string":"nowhere","new_string":"x"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "edit Program.cs");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// Order matters: a file written BEFORE the failure does not close it. The write has to be the
    /// recovery, not something that happened to touch the same file earlier.
    /// </summary>
    [Fact]
    public async Task A_write_that_came_before_the_failure_does_not_close_it()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("write_file", """{"path":"Program.cs","content":"// first\n"}"""),
                    Turn.Calls1("edit_file",
                                """{"path":"Program.cs","old_string":"nowhere","new_string":"x"}""", "c2"),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "write then edit Program.cs");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }
}
