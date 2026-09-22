namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// The whole path, end to end: a run nobody wrote a template for, decided by a command instead of
/// by what the model said about itself.
///
/// <para>These call the real shell. That is the point — the verdict has to come from an exit code
/// that something actually produced, and a test with a faked one would only prove that this file
/// agrees with itself.</para>
/// </summary>
public sealed class CheckedWithoutATemplateTests
{
    /// <summary>A planner answer with checks in it, as the planner is now asked to produce.</summary>
    private static string PlanWith(string command)
        => $$"""
           {"disposition":"quick_action","title":"write the file","steps":[],
            "checks":[{"name":"it is there","command":"{{command}}"}]}
           """;

    /// <summary>
    /// The model says it is done, the check says otherwise, and the check wins. Before this the
    /// run had nothing to consult but the sentence "Done."
    /// </summary>
    [Fact]
    public async Task A_failing_check_fails_a_run_the_model_called_done()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(PlanWith("exit /b 1")),
                    Turn.Calls1("write_file", """{"path":"notes.md","content":"# notes\n"}"""),
                    Turn.Says("Done."),
                    // The repair attempt the engine allows, and its verdict after it.
                    Turn.Says("I cannot make that check pass."),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "write notes.md");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
        Assert.True(fx.Exists("notes.md"), "the work itself still happened");
    }

    /// <summary>And the same run with a check that passes finishes, as it always did.</summary>
    [Fact]
    public async Task A_passing_check_leaves_the_run_alone()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(PlanWith("exit /b 0")),
                    Turn.Calls1("write_file", """{"path":"notes.md","content":"# notes\n"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "write notes.md");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// THE PROPERTY THAT MAKES THIS SAFE, proved against cmd.exe itself. The planner guessed a
    /// PowerShell cmdlet for a check and sent it to <c>run_command</c>, which is cmd.exe. Nothing
    /// ran. A check nobody asked for and that never ran must not fail a run — and the only reason
    /// the engine can tell is <c>ShellOutcome</c>, added 2026-09-20 for the opposite problem.
    /// </summary>
    [Fact]
    public async Task A_proposed_check_the_shell_would_not_start_does_not_fail_the_run()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(PlanWith("Select-String -Path notes.md -Pattern notes")),
                    Turn.Calls1("write_file", """{"path":"notes.md","content":"# notes\n"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "write notes.md");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// A plan that proposes nothing is judged as it was before any of this: the run still happens,
    /// and nothing new holds it back. Most requests are this one.
    /// </summary>
    [Fact]
    public async Task A_plan_with_no_checks_runs_exactly_as_before()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"explain","steps":[],"checks":[]}"""),
                    Turn.Calls1("write_file", """{"path":"notes.md","content":"# notes\n"}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "write notes.md");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }
}
