namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// A step that did its work is not failed for a call that never happened.
///
/// <para><b>Two runs, half an hour apart, both thrown away on 2026-09-22.</b></para>
///
/// <para>18:01 — a step verified five wiki pages, found two new drifts, restored a report from
/// HEAD, extended it to 550 lines and gave its final answer. Then it asked to delete
/// <c>.enactive/scratch/DRIFT_head.md</c> — a scratch file of its own that nothing needed — and the
/// person said no. The step was marked Incomplete for "the user did not permit this action", four
/// dependent steps were skipped, and the run failed.</para>
///
/// <para>18:28 — a step made 114 successful calls over four and a half minutes, rewrote the same
/// report and gave its final answer. Four and a half minutes earlier it had written
/// <c>git {"args": show HEAD:Docs/DRIFT_ollama.md}</c> — quotes missing, so the arguments did not
/// parse and git was never started. It never repeated the call, so nothing closed it. Same
/// outcome: Incomplete, four steps skipped, run failed.</para>
///
/// <para><b>The rule.</b> A call that never happened — unreadable arguments, a shell that would not
/// start the line, or a refusal by a person or a policy — leaves no residue behind it: no
/// half-written file, no broken build. It stops counting once the step has CHANGED something. Not
/// "once anything worked": a step that sent a malformed write and then read three files has still
/// written nothing, and forgiving that would reopen the hole this whole mechanism exists to close.
/// A call that RAN and failed is never forgiven, however much else the step did.</para>
/// </summary>
public sealed class ACallThatNeverHappenedTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"do the work","steps":[]}""";

    private static async Task<IReadOnlyList<WorkEvent>> RunAsync(
        EngineFixture fx, FakeChatProvider agent, IDecisionHandler? decisions = null)
        => await fx.RunAsync(
            fx.Build(agent, EngineFixture.Role("developer"), decisions: decisions),
            "do the work");

    /// <summary>The run's own verdict — a quick action has no step card to read it off.</summary>
    private static string OutcomeOf(IEnumerable<WorkEvent> events)
        => events.LastOrDefault(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed)
                 ?.Kind.ToString() ?? "(neither)";

    private const string Completed = nameof(EventKind.TaskCompleted);
    private const string Failed = nameof(EventKind.TaskFailed);

    /// <summary>
    /// The 18:28 run, in miniature: a call whose arguments did not parse, never repeated, and a
    /// step that then wrote the file it was there to write.
    /// </summary>
    [Fact]
    public async Task A_call_that_did_not_parse_does_not_sink_a_step_that_wrote_its_file()
    {
        using var fx = new EngineFixture();

        var agent = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("git", """{"args": show HEAD:report.md}"""),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the work"}"""),
            Turn.Says("Wrote the report."));

        var events = await RunAsync(fx, agent);

        Assert.Equal(Completed, OutcomeOf(events));
        Assert.Equal("the work", fx.Read("report.md"));
    }

    /// <summary>
    /// And the hole stays shut: reading three files is not doing the work, so a write that never
    /// parsed still ends the step — which is the case this mechanism was built for.
    /// </summary>
    [Fact]
    public async Task A_write_that_did_not_parse_still_sinks_a_step_that_only_read()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "one");

        var agent = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("write_file", """{"path": report.md, "content":"x"}"""),
            Turn.Calls1("read_file", """{"path":"a.md"}"""),
            Turn.Says("Had a look."));

        var events = await RunAsync(fx, agent);

        Assert.Equal(Failed, OutcomeOf(events));
    }

    /// <summary>
    /// The 18:01 run: the person was asked, the person said no, and that is an ANSWER. The step had
    /// already written what it was for.
    /// </summary>
    [Fact]
    public async Task A_refusal_by_the_person_does_not_sink_a_step_that_wrote_its_file()
    {
        using var fx = new EngineFixture();
        fx.Write("scratch.md", "leftovers");

        var agent = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the work"}"""),
            Turn.Calls1("delete_file", """{"path":"scratch.md"}"""),
            Turn.Says("Wrote the report; left the scratch file alone."));

        var events = await RunAsync(fx, agent, decisions: new ScriptedDecisionHandler("deny"));

        Assert.Equal(Completed, OutcomeOf(events));
        Assert.Equal("the work", fx.Read("report.md"));

        // Still on record: the reviewer and the reader see that it was asked for and refused.
        Assert.Contains(events, e => e.Kind == EventKind.DecisionRequested || e.Kind == EventKind.DecisionResolved);
    }

    /// <summary>
    /// A refusal is forgiven, a FAILURE is not. A command that started and came out non-zero is
    /// evidence about the workspace, and no amount of other work makes it not have happened.
    /// </summary>
    [Fact]
    public async Task A_command_that_ran_and_failed_still_sinks_the_step()
    {
        using var fx = new EngineFixture();

        var agent = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"exit /b 3"}"""),
            Turn.Calls1("write_file", """{"path":"report.md","content":"the work"}"""),
            Turn.Says("Wrote the report."));

        var events = await RunAsync(fx, agent);

        Assert.Equal(Failed, OutcomeOf(events));
    }
}
