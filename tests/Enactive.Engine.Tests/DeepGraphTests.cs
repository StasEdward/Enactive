namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// The two shapes <c>ParallelStepTests</c> left uncovered, recorded in <c>PLAN_v2.md</c> §11: a
/// graph DEEPER than one layer of branches and a join, and a CYCLE with more than one step in
/// flight.
///
/// <para>Every parallel test before this one is one layer wide: branches from the root, then a join.
/// That shape never asks the two questions a real plan does — whether a step whose dependency is
/// itself a JOIN waits properly, and whether a failure two layers up reaches all the way down. Both
/// are answered by the same cascade and the same readiness check, and neither had ever been run
/// against anything with a middle.</para>
///
/// <para>The cycle case is the one the engine detects rather than prevents: nothing stops a planner
/// emitting <c>A depends on B, B depends on A</c>, and what happens then is a property of the
/// dispatcher, not of the plan.</para>
/// </summary>
public sealed class DeepGraphTests
{
    /// <summary>
    /// Three layers, two joins, six steps:
    /// <code>
    ///           Base
    ///           /  \
    ///   LeftLower  RightLower
    ///        |          |
    ///   LeftUpper  RightUpper
    ///           \  /
    ///           Peak
    /// </code>
    /// The middle layer is the point: LeftUpper's dependency is not the root, and Peak's
    /// dependencies are not branches — they are themselves the results of a layer.
    ///
    /// <para>No title is a PREFIX of another, deliberately. A step is found in the event stream by
    /// its summary, and "Left" would match "LeftUpper" too — a test that reads the wrong card is
    /// worse than one that is missing.</para>
    /// </summary>
    private const string ThreeLayers = """
        {"disposition":"task","title":"Three layers",
         "steps":[{"title":"Base","dependsOn":[]},
                  {"title":"LeftLower","dependsOn":[0]},
                  {"title":"RightLower","dependsOn":[0]},
                  {"title":"LeftUpper","dependsOn":[1]},
                  {"title":"RightUpper","dependsOn":[2]},
                  {"title":"Peak","dependsOn":[3,4]}]}
        """;

    private static readonly string[] Layered =
        { "Base", "LeftLower", "RightLower", "LeftUpper", "RightUpper", "Peak" };

    private static ByStepChatProvider Scripted(string plan, params string[] titles)
    {
        var provider = new ByStepChatProvider(plan);
        foreach (var title in titles)
            provider.Step(title, Turn.Says($"CONCLUSION OF {title}."));
        return provider;
    }

    private static int Started(List<WorkEvent> events, string title)
        => events.FindIndex(e => e.Kind == EventKind.StepStarted && e.Summary.Contains(title, StringComparison.Ordinal));

    private static int Finished(List<WorkEvent> events, string title)
        => events.FindIndex(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains(title, StringComparison.Ordinal));

    private static WorkEvent Card(List<WorkEvent> events, string title)
        => events.Single(e => e.Kind == EventKind.StepCompleted
                              && e.Summary.Contains(title, StringComparison.Ordinal));

    // ── deeper than one layer ───────────────────────────────────────────────

    /// <summary>
    /// Every step runs, and every step starts only after everything it depends on has finished —
    /// including the two whose dependencies are in the MIDDLE of the graph rather than at its root.
    /// </summary>
    [Fact]
    public async Task Every_step_of_a_three_layer_graph_waits_for_the_layer_below_it()
    {
        using var fixture = new EngineFixture();
        var provider = Scripted(ThreeLayers, Layered);

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 3), "build it in layers");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(6, events.Count(e => e.Kind == EventKind.StepCompleted));

        // Edge by edge, off the event stream — the only place ordering is observable from outside.
        var edges = new[]
        {
            ("Base", "LeftLower"), ("Base", "RightLower"),
            ("LeftLower", "LeftUpper"), ("RightLower", "RightUpper"),
            ("LeftUpper", "Peak"), ("RightUpper", "Peak")
        };

        foreach (var (before, after) in edges)
        {
            var finished = Finished(events, before);
            var started = Started(events, after);
            Assert.True(finished >= 0, $"{before} never finished");
            Assert.True(started >= 0, $"{after} never started");
            Assert.True(finished < started, $"{after} started before {before} had finished");
        }

        // And the middle layer really did overlap: two independent branches at three in flight.
        Assert.True(provider.PeakConcurrency > 1,
            "nothing overlapped; this ran the serial path and proves nothing about the dispatcher");
    }

    /// <summary>
    /// A step two layers up is told what its ANCESTORS concluded, not only its parents. The digest
    /// is the only thing that crosses between parallel steps, and it is one flat list for the run —
    /// so a graph with a middle is where "did the list carry" stops being obvious.
    /// </summary>
    [Fact]
    public async Task A_step_at_the_top_is_told_what_every_layer_below_it_concluded()
    {
        using var fixture = new EngineFixture();
        var provider = Scripted(ThreeLayers, Layered);

        await fixture.RunAsync(fixture.Build(provider, maxParallelSteps: 3), "build it in layers");

        var top = provider.RequestsFor("Peak").First();
        var whole = string.Join("\n", top.Messages.Select(m => m.Content ?? ""));

        foreach (var title in Layered.Where(t => t != "Peak"))
            Assert.Contains($"CONCLUSION OF {title}", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure in the MIDDLE layer. The cascade has to walk two edges — LeftLower ✗ → LeftUpper →
    /// Peak — while the right-hand side is still in flight, and it must not touch the right-hand
    /// side.
    ///
    /// <para>One layer of branches never tests this: there, a failed branch's only dependent is the
    /// join, so one hop is all the cascade is ever asked for.</para>
    /// </summary>
    [Fact]
    public async Task A_failure_in_the_middle_layer_skips_everything_above_it_and_nothing_beside_it()
    {
        using var fixture = new EngineFixture();

        var provider = Scripted(ThreeLayers, "Base", "RightLower", "RightUpper", "Peak");
        // A step that never says anything and never calls anything is stopped as stuck, which is the
        // simplest way to make one step of a plan fail without failing the others.
        var stuck = Turn.Calls1("read_file", """{"path":"nowhere.txt"}""", "r");
        provider.Step("LeftLower", stuck, stuck, stuck, stuck, stuck, stuck);

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 3), "build it in layers");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());

        // The failed step, and everything above it.
        Assert.NotEqual(StepOutcomeKind.Succeeded, Card(events, "LeftLower").StepOutcome());
        Assert.Equal(StepOutcomeKind.Skipped, Card(events, "LeftUpper").StepOutcome());
        Assert.Equal(StepOutcomeKind.Skipped, Card(events, "Peak").StepOutcome());

        // The other side of the graph is untouched — it depends on none of it.
        Assert.Equal(StepOutcomeKind.Succeeded, Card(events, "Base").StepOutcome());
        Assert.Equal(StepOutcomeKind.Succeeded, Card(events, "RightLower").StepOutcome());
        Assert.Equal(StepOutcomeKind.Succeeded, Card(events, "RightUpper").StepOutcome());

        // Every step of the plan accounted for. A step with no card has no outcome, and the run's
        // outcome is the aggregate of its steps' - so one would quietly not count.
        Assert.Equal(6, events.Count(e => e.Kind == EventKind.StepCompleted));
    }

    // ── a cycle, with steps in flight ───────────────────────────────────────

    /// <summary>
    /// A whole plan that depends on itself. Nothing is ever ready, the dispatcher has nothing to
    /// hand out, and the run must say WHY rather than reporting success over work it never did.
    /// </summary>
    [Fact]
    public async Task A_plan_that_is_all_cycle_stops_and_says_so()
    {
        using var fixture = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"Round and round",
             "steps":[{"title":"Alpha","dependsOn":[1]},
                      {"title":"Beta","dependsOn":[0]}]}
            """;

        var provider = Scripted(plan, "Alpha", "Beta");

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 2), "an impossible plan");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                     && e.Summary.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// The half-and-half case, and the one that matters: two steps run, and the rest of the plan is
    /// a cycle among themselves.
    ///
    /// <para>The run's outcome is built from its STEPS' outcomes. A step left Pending has none — and
    /// this is exactly the reasoning that made the LIMIT path call <c>AbandonPending</c> and record
    /// every abandoned step as Skipped, with the comment "a step with no recorded outcome would
    /// quietly not count". The cycle path was left with the same hole: the steps it cannot run stay
    /// Pending, get no card, and are counted by nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_cycle_behind_a_runnable_prefix_accounts_for_every_step()
    {
        using var fixture = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"Two then a knot",
             "steps":[{"title":"One","dependsOn":[]},
                      {"title":"Two","dependsOn":[0]},
                      {"title":"Knot A","dependsOn":[3]},
                      {"title":"Knot B","dependsOn":[2]}]}
            """;

        var provider = Scripted(plan, "One", "Two", "Knot A", "Knot B");

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 2), "two steps and a knot");

        // The runnable prefix ran.
        Assert.Equal(StepOutcomeKind.Succeeded, Card(events, "One").StepOutcome());
        Assert.Equal(StepOutcomeKind.Succeeded, Card(events, "Two").StepOutcome());

        // The knot did not, and says so — as the plan's own steps, not as silence.
        Assert.Equal(StepOutcomeKind.Skipped, Card(events, "Knot A").StepOutcome());
        Assert.Equal(StepOutcomeKind.Skipped, Card(events, "Knot B").StepOutcome());
        Assert.Equal(4, events.Count(e => e.Kind == EventKind.StepCompleted));

        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                     && e.Summary.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// A step that depends on ITSELF is a cycle of one. Reachable from a plan restored from a
    /// checkpoint, where the dependency ids are data rather than indices the planner filtered.
    /// </summary>
    [Fact]
    public void A_step_that_depends_on_itself_is_never_ready()
    {
        var id = Guid.NewGuid();
        var plan = new Plan(Guid.NewGuid(), new[]
        {
            new PlanStep(id, "Ouroboros", StepStatus.Pending, new[] { id })
        });

        var scheduler = new DagScheduler(plan);

        Assert.Empty(scheduler.NextReadyBatch(4));
        Assert.True(scheduler.HasPending);
    }

    // ── the scheduler's own reading of a dependency ─────────────────────────

    /// <summary>
    /// A dependency the plan does not contain is NOT satisfied.
    ///
    /// <para>An absence is not an answer, and the absent thing here is the whole reason to wait. A
    /// step whose prerequisite cannot be found has not had its prerequisite met — it has a plan
    /// nobody can carry out, which is the cycle detector's business, not a reason to run.</para>
    ///
    /// <para>Reachable from a plan rebuilt out of stored data: <c>PlanOf</c> takes a checkpoint's
    /// dependency ids as given, and a checkpoint written by an older build, hand-edited, or
    /// truncated can name a step that is not in the list beside it.</para>
    /// </summary>
    [Fact]
    public void A_dependency_that_is_not_in_the_plan_is_not_satisfied()
    {
        var orphan = new PlanStep(
            Guid.NewGuid(), "Depends on a step nobody has", StepStatus.Pending,
            new[] { Guid.NewGuid() });

        var scheduler = new DagScheduler(new Plan(Guid.NewGuid(), new[] { orphan }));

        Assert.Empty(scheduler.NextReadyBatch(4));
        Assert.True(scheduler.HasPending);
    }
}
