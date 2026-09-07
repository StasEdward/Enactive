namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// M2 of Docs/TASK_TEMPLATES_PLAN.md, and the reason the rest of it exists.
///
/// <para>Everything the engine had to decide whether work was finished went through a language
/// model: the worker's own closing sentence, and a reviewer's opinion of free text. On 2026-09-07
/// that reviewer failed a correct run over a defect it had invented, complete with a line number,
/// and earlier it had passed a guide full of package names that do not exist. An exit code is not
/// an opinion.</para>
/// </summary>
public sealed class SuccessCriteriaTests
{
    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    /// <summary>A command that reports the exit code we ask it for, on either platform.</summary>
    private static string ExitWith(int code)
        => OperatingSystem.IsWindows() ? $"cmd /c exit {code}" : $"exit {code}";

    private static SuccessCriterionDefinition Criterion(
        string name, int exitCode, bool required = true, int expected = 0)
        => new(name, ExitWith(exitCode), expected, required);

    /// <summary>A provider that writes a file and then declares victory, which is all it ever had to do.</summary>
    private static FakeChatProvider ClaimsSuccess() => new(
        Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
        Turn.Calls1("write_file", """{"path":"ok.txt","content":"hello"}"""),
        Turn.Says("All done — everything builds and the tests pass."));

    // ── the point of the whole milestone ────────────────────────────────────

    /// <summary>
    /// The decisive one. The model says the work is done and the build says otherwise; the build
    /// wins. Before this, the sentence above was the last word in the run.
    /// </summary>
    [Fact]
    public async Task A_failing_check_beats_the_model_saying_it_is_done()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            ClaimsSuccess(),
            successCriteria: new[] { Criterion("Solution builds", exitCode: 1) });

        var events = await fx.RunAsync(orchestrator, "do the thing");
        var terminal = Terminal(events);

        Assert.Equal(EventKind.TaskFailed, terminal.Kind);
        Assert.Equal(RunOutcomeKind.Failed, terminal.Outcome());
        Assert.Contains("Solution builds", terminal.OutcomeReason());

        // The model's own claim is still in the run - it just is not the verdict any more.
        Assert.Contains(events, e => (e.Summary ?? "").Contains("All done"));
    }

    [Fact]
    public async Task A_passing_check_leaves_a_good_run_alone()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            ClaimsSuccess(),
            successCriteria: new[] { Criterion("Solution builds", exitCode: 0) });

        var terminal = Terminal(await fx.RunAsync(orchestrator, "do the thing"));

        Assert.Equal(EventKind.TaskCompleted, terminal.Kind);
        Assert.Equal(RunOutcomeKind.Completed, terminal.Outcome());
    }

    /// <summary>Every check is reported, passing or not - a run report that only lists failures
    /// cannot be used to answer "was this actually verified".</summary>
    [Fact]
    public async Task Every_check_leaves_an_event_carrying_its_result_as_values()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            ClaimsSuccess(),
            successCriteria: new[]
            {
                Criterion("Solution builds", exitCode: 0),
                Criterion("Tests pass", exitCode: 1)
            });

        var events = await fx.RunAsync(orchestrator, "do the thing");
        var checks = events.Where(e => e.Kind == EventKind.CriterionEvaluated).ToArray();

        Assert.Equal(2, checks.Length);
        Assert.Equal("Passed", checks[0].CriterionOutcomeName());
        Assert.Equal("Solution builds", checks[0].CriterionName());
        Assert.Equal("Failed", checks[1].CriterionOutcomeName());
        Assert.Equal("Tests pass", checks[1].CriterionName());
    }

    /// <summary>
    /// An optional criterion is information, not a gate. Without this distinction every check a
    /// person adds out of curiosity becomes a way to fail their run.
    /// </summary>
    [Fact]
    public async Task An_optional_check_reports_and_changes_nothing()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            ClaimsSuccess(),
            successCriteria: new[] { Criterion("Nice to have", exitCode: 1, required: false) });

        var events = await fx.RunAsync(orchestrator, "do the thing");
        var terminal = Terminal(events);

        Assert.Equal(RunOutcomeKind.Completed, terminal.Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated);
    }

    /// <summary>A criterion may expect a non-zero code: 'git diff --exit-code' means something by
    /// returning 1, and judging it by the tool's own success flag would get it backwards.</summary>
    [Fact]
    public async Task A_check_may_expect_a_non_zero_exit_code()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            ClaimsSuccess(),
            successCriteria: new[] { Criterion("Nothing left to commit", exitCode: 1, expected: 1) });

        Assert.Equal(RunOutcomeKind.Completed,
            Terminal(await fx.RunAsync(orchestrator, "do the thing")).Outcome());
    }

    /// <summary>
    /// A run that already went wrong keeps the reason it went wrong. Burying "the reviewer rejected
    /// this" under a build result would lose the more useful of the two, and cost a build to learn
    /// nothing.
    /// </summary>
    [Fact]
    public async Task A_run_that_already_failed_is_not_re_judged_by_its_checks()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Calls1("write_file", """{"path":"ok.txt","content":"hello"}"""))
        { WhenExhausted = Turn.Says("done") };
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Fail("the work is wrong") };

        var orchestrator = fx.Build(
            worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            successCriteria: new[] { Criterion("Solution builds", exitCode: 0) });

        var events = await fx.RunAsync(orchestrator, "do the thing");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.DoesNotContain(events, e => e.Kind == EventKind.CriterionEvaluated);
    }

    [Fact]
    public async Task A_run_with_no_criteria_behaves_exactly_as_it_did_before()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(fx.Build(ClaimsSuccess()), "do the thing");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.DoesNotContain(events, e => e.Kind == EventKind.CriterionEvaluated);
    }

    // ── the rules, without the engine ───────────────────────────────────────

    /// <summary>
    /// Fails closed. A check that could not be evaluated has verified nothing, exactly as a reviewer
    /// that cannot answer has approved nothing - but it is not a Failure either, because the work
    /// may well be fine and what broke was our ability to say so.
    /// </summary>
    [Fact]
    public void A_check_that_could_not_run_is_not_a_pass_and_not_an_accusation()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("Solution builds", "dotnet build", true,
                                CriterionOutcome.Unknown, null, "the policy forbids run_command")
        });

        Assert.Equal(RunOutcomeKind.Incomplete, report.Apply(RunOutcomeKind.Completed));
        Assert.Contains("could not be checked", report.Explain());
    }

    [Fact]
    public void A_failure_outranks_a_check_that_could_not_run()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("A", "a", true, CriterionOutcome.Unknown, null, null),
            new CriterionResult("B", "b", true, CriterionOutcome.Failed, 2, null)
        });

        Assert.Equal(RunOutcomeKind.Failed, report.Apply(RunOutcomeKind.Completed));
    }

    [Theory]
    [InlineData(RunOutcomeKind.Failed)]
    [InlineData(RunOutcomeKind.Incomplete)]
    [InlineData(RunOutcomeKind.Cancelled)]
    public void Checks_can_hold_a_run_back_and_never_promote_one(RunOutcomeKind already)
    {
        var allGood = new SuccessReport(new[]
        {
            new CriterionResult("A", "a", true, CriterionOutcome.Passed, 0, null)
        });

        Assert.Equal(already, allGood.Apply(already));
    }

    [Fact]
    public void With_nothing_to_check_the_outcome_is_untouched()
    {
        Assert.Equal(RunOutcomeKind.Completed,
            SuccessReport.NothingToCheck.Apply(RunOutcomeKind.Completed));
        Assert.Null(SuccessReport.NothingToCheck.Explain());
    }
}
