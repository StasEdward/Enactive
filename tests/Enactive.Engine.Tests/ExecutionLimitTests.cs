namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// M3b of Docs/TASK_TEMPLATES_PLAN.md: a template's limits actually limit something.
///
/// <para><see cref="ExecutionLimits"/> shipped in M0 as data and was read by NOTHING - a template
/// could declare <c>MaxSteps: 12</c> and the run would take fifty. A limit that is stored, shown in
/// an editor and never enforced is worse than no limit, because somewhere it is written down as a
/// guarantee.</para>
/// </summary>
public sealed class ExecutionLimitTests
{
    private const string FourStepPlan = """
        {"disposition":"task","title":"four steps",
         "steps":[{"title":"one","dependsOn":[]},{"title":"two","dependsOn":[0]},
                  {"title":"three","dependsOn":[1]},{"title":"four","dependsOn":[2]}]}
        """;

    private static WorkEvent Terminal(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    private static int StepsThatRan(IEnumerable<WorkEvent> events)
        => events.Count(e => e.Kind == EventKind.StepStarted);

    // ── through the engine ──────────────────────────────────────────────────

    /// <summary>
    /// The plan has four steps and the budget allows two. The run stops after two, the rest are
    /// recorded as skipped rather than left in limbo, and the terminal event says which limit it
    /// was - without that it reports "2 step(s) skipped" and no reason for them.
    /// </summary>
    [Fact]
    public async Task A_step_limit_stops_the_run_and_says_so()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(FourStepPlan)) { WhenExhausted = Turn.Says("step done") };

        var orchestrator = fx.Build(provider, limits: new ExecutionLimits(MaxSteps: 2));
        var events = await fx.RunAsync(orchestrator, "do four things");

        Assert.Equal(2, StepsThatRan(events));

        var terminal = Terminal(events);
        Assert.Equal(RunOutcomeKind.Incomplete, terminal.Outcome());
        Assert.Contains("limit of 2 step(s)", terminal.OutcomeReason());

        // Every step has an outcome. A step left Pending would have no recorded outcome at all, and
        // the run's outcome is built from its steps' - so it would quietly not count.
        var completions = events.Where(e => e.Kind == EventKind.StepCompleted).ToArray();
        Assert.Equal(4, completions.Length);
        Assert.Equal(2, completions.Count(e => e.StepOutcome() == StepOutcomeKind.Skipped));
    }

    [Fact]
    public async Task A_plan_that_fits_its_limit_is_untouched()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(FourStepPlan)) { WhenExhausted = Turn.Says("step done") };

        var orchestrator = fx.Build(provider, limits: new ExecutionLimits(MaxSteps: 9));
        var events = await fx.RunAsync(orchestrator, "do four things");

        Assert.Equal(4, StepsThatRan(events));
        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
    }

    [Fact]
    public async Task A_run_with_no_limits_behaves_exactly_as_it_did_before()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(FourStepPlan)) { WhenExhausted = Turn.Says("step done") };

        var events = await fx.RunAsync(fx.Build(provider), "do four things");

        Assert.Equal(4, StepsThatRan(events));
        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.DoesNotContain(events, e => (e.Summary ?? "").Contains("reached its limit"));
    }

    /// <summary>
    /// The tokens counted are the RUN's, not the executor's. Planning and review are charged to the
    /// same budget - a reviewer on a large cloud model is routinely the larger half of a run - and
    /// the planner alone is enough to exhaust a small one here.
    /// </summary>
    [Fact]
    public async Task Planning_and_review_are_charged_to_the_same_budget()
    {
        using var fx = new EngineFixture();
        // The PLANNER reports tokens; nothing else in this script does. If planning were not charged
        // to the run's budget this test could not fail, whatever the limit.
        var provider = new FakeChatProvider(Turn.Says(FourStepPlan).Reporting(prompt: 40, completion: 10))
        { WhenExhausted = Turn.Says("step done") };

        var orchestrator = fx.Build(provider, limits: new ExecutionLimits(MaxTokens: 20));
        var events = await fx.RunAsync(orchestrator, "do four things");

        // The plan alone spent 50, so the budget is gone before the first step is dispatched.
        Assert.Equal(0, StepsThatRan(events));
        Assert.Equal(RunOutcomeKind.Incomplete, Terminal(events).Outcome());
        Assert.Contains("token(s)", Terminal(events).OutcomeReason());
    }

    /// <summary>A quick action runs no plan steps, so the retry is the only place more spending is
    /// chosen rather than already under way - and it is where a spent budget has to stop it.</summary>
    [Fact]
    public async Task A_quick_action_stops_when_its_budget_is_gone()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a file"}""")
                .Reporting(prompt: 40, completion: 10))
        { WhenExhausted = Turn.Says("done") };

        var orchestrator = fx.Build(worker, limits: new ExecutionLimits(MaxTokens: 20));
        var events = await fx.RunAsync(orchestrator, "write ok.txt");

        Assert.Equal(RunOutcomeKind.Incomplete, Terminal(events).Outcome());
        Assert.Contains("token(s)", Terminal(events).OutcomeReason());
    }

    // ── the budget on its own ───────────────────────────────────────────────

    [Fact]
    public void Nothing_set_means_nothing_is_ever_exhausted()
    {
        var budget = new RunBudget(null, DateTimeOffset.UtcNow);

        for (var i = 0; i < 1000; i++)
        {
            budget.StepStarted();
            budget.TokensUsed(10_000, 10_000);
        }

        Assert.Null(budget.Exhausted);
    }

    [Fact]
    public void A_step_limit_is_reached_by_the_step_that_reaches_it()
    {
        var budget = new RunBudget(new ExecutionLimits(MaxSteps: 2), DateTimeOffset.UtcNow);

        budget.StepStarted();
        Assert.Null(budget.Exhausted);

        budget.StepStarted();
        Assert.Contains("2 step(s)", budget.Exhausted);
    }

    [Fact]
    public void Tokens_accumulate_across_every_phase()
    {
        var budget = new RunBudget(new ExecutionLimits(MaxTokens: 100), DateTimeOffset.UtcNow);

        budget.TokensUsed(30, 20);    // planning
        budget.TokensUsed(20, 10);    // execution
        Assert.Null(budget.Exhausted);
        Assert.Equal(80, budget.TokensSpent);

        budget.TokensUsed(10, 10);    // review takes it over
        Assert.Contains("100 token(s)", budget.Exhausted);
    }

    /// <summary>The clock is injected so a duration limit can be tested without waiting for it.</summary>
    [Fact]
    public void A_duration_limit_is_reached_by_the_clock()
    {
        var started = DateTimeOffset.UtcNow;
        var now = started;

        var budget = new RunBudget(new ExecutionLimits(MaxDurationSeconds: 60), started, () => now);
        Assert.Null(budget.Exhausted);

        now = started.AddSeconds(59);
        Assert.Null(budget.Exhausted);

        now = started.AddSeconds(60);
        Assert.Contains("60 second(s)", budget.Exhausted);
    }

    /// <summary>
    /// Three limits, and the one that is actually hit is the one named. A message about tokens on a
    /// run that ran out of time sends a person to change the wrong number.
    /// </summary>
    [Fact]
    public void The_limit_that_was_hit_is_the_one_reported()
    {
        var started = DateTimeOffset.UtcNow;
        var now = started;
        var budget = new RunBudget(
            new ExecutionLimits(MaxSteps: 50, MaxTokens: 50_000, MaxDurationSeconds: 30), started, () => now);

        budget.StepStarted();
        budget.TokensUsed(100, 100);
        now = started.AddSeconds(31);

        var why = budget.Exhausted;
        Assert.Contains("second(s)", why);
        Assert.DoesNotContain("token", why);
        Assert.DoesNotContain("step", why);
    }
}
