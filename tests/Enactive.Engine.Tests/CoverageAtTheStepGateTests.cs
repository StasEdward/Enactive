namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Plan Phase 1.3: a list a step consumes is checked at that step's own gate - after it ends, before any review - and
/// not only once the run is over. A step that left items uncovered is told which and tried again on a budget of the
/// gate's own; the reviewer is not asked about work the engine can already see is short.
/// </summary>
public sealed class CoverageAtTheStepGateTests
{
    private static string[] Pages(int n) => Enumerable.Range(1, n).Select(i => $"wiki/p{i:00}.md").ToArray();

    private static string PlanFor(string evidence = "mentioned") => """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages","evidence":"EVIDENCE"}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """.Replace("EVIDENCE", evidence, StringComparison.Ordinal);

    private static EngineFixture Wiki(int pages)
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        foreach (var page in Pages(pages))
            fx.Write(page, $"# {page}\nabout it\n");
        return fx;
    }

    private static Turn[] Found(string[] pages)
        => [Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { pages }), "s1"), Turn.Says("Found them.")];

    private static Turn Read(string page) => Turn.Calls1("read_file", JsonSerializer.Serialize(new { path = page }), "r-" + page);

    private static Turn Notes(IEnumerable<string> pages)
        => Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes = pages.ToDictionary(p => p, p => $"{p} is fine") }), "s2");

    private static WorkEvent StepEnd(List<WorkEvent> events, int step)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == step);

    private static WorkEvent[] GateFailures(List<WorkEvent> events, int step)
        => events.Where(e => e.Kind == EventKind.ReviewFailed && e.StepNo() == step
                             && e.Summary.Contains("engine's coverage check", StringComparison.Ordinal)).ToArray();

    private static ChatRequest[] ReviewsOf(FakeChatProvider reviewer, int step)
        => reviewer.Requests.Where(r => r.Messages.Any(m => m.Content?.Contains($"THIS STEP ({step}):", StringComparison.Ordinal) == true)).ToArray();

    // ── the decision ─────────────────────────────────────────────────────────────────────

    private static Enactive.Core.Templates.SuccessCriterionDefinition Coverage(int results, EvidenceKind evidence = EvidenceKind.Call)
        => new("Every pages item is covered", "covers_all", Required: true, Origin: Enactive.Core.Templates.CriterionOrigin.Declared)
        {
            Typed = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "pages",
                ResultsStep: results, ResultsField: "notes", Evidence: evidence)
        };

    private static StepOutput Output(int stepNo, object values, IReadOnlyList<ItemEvidence>? items = null)
        => new(stepNo, $"step {stepNo}", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow, JsonSerializer.Serialize(values), [], 1, []) { Items = items };

    /// <summary>The gate decides the coverage whose results are this step's, and no other.</summary>
    [Fact]
    public void The_gate_decides_only_the_coverage_whose_results_are_this_steps()
        => Assert.Equal([Coverage(1)], StepCoverageGate.For([Coverage(1), Coverage(2), new("other", "dotnet test")], 1));

    /// <summary>
    /// A source that handed on no list leaves this step nothing to cover and a retry nothing to change: left to the
    /// run's end, which says so. A step whose candidate is missing has handed on nothing, and covered nothing.
    /// </summary>
    [Fact]
    public void Without_a_list_there_is_nothing_for_the_gate_and_without_a_result_nothing_is_covered()
    {
        Assert.Null(StepCoverageGate.Check([Coverage(1)], [], Output(2, new { notes = new { } }), 2));

        var gate = StepCoverageGate.Check([Coverage(1)], [Output(1, new { pages = Pages(2) })], null, 2)!;
        Assert.False(gate.Passed);
        Assert.Equal("covered 0 of 2; not covered: wiki/p01.md (no result); wiki/p02.md (no result).", gate.Notes);
        Assert.Contains("- wiki/p01.md: a result in 'notes', and mentioned: a call that succeeded with the item among its arguments - found: nothing",
            gate.RepairAdvice, StringComparison.Ordinal);
    }

    /// <summary>A stale output of this step - from before a retry - is not its candidate: the candidate is.</summary>
    [Fact]
    public void The_candidate_stands_for_the_step_not_what_it_handed_on_before()
    {
        var pages = Pages(1);
        var covered = Output(2, new { notes = new Dictionary<string, string> { [pages[0]] = "ok" } }, [new ItemEvidence("notes", pages[0], [EvidenceKind.Call], null)]);

        var gate = StepCoverageGate.Check([Coverage(1)], [Output(1, new { pages }), covered], Output(2, new { notes = new { } }), 2)!;

        Assert.False(gate.Passed);
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    /// <summary>THE ONE THAT MATTERS: twelve consumed, two covered - rejected by the engine, and the reviewer never asked.</summary>
    [Fact]
    public async Task A_step_that_covers_2_of_12_is_rejected_by_the_gate_without_asking_the_reviewer()
    {
        var pages = Pages(12);
        using var fx = Wiki(12);
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), Read(pages[0]), Read(pages[1]), Notes(pages[..2]), Turn.Says("Reviewed two.")]);
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "review every wiki page");

        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.StartsWith("covered 2 of 12; not covered: wiki/p03.md (no result)", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Empty(ReviewsOf(reviewer, 2));
        Assert.Single(ReviewsOf(reviewer, 1));
        // The gate's own budget with review on: as many retries as the review has (1), so two attempts.
        Assert.Equal(2, GateFailures(events, 2).Length);
        Assert.Contains("covered 2 of 12", GateFailures(events, 2)[0].Summary, StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>Review off: the gate still runs, on its own budget - one retry, then rejected.</summary>
    [Fact]
    public async Task With_review_off_the_gate_still_runs_on_its_own_budget()
    {
        var pages = Pages(12);
        using var fx = Wiki(12);
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), Read(pages[0]), Read(pages[1]), Notes(pages[..2]), Turn.Says("Reviewed two.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.StartsWith("covered 2 of 12", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(2, GateFailures(events, 2).Length);
    }

    /// <summary>The retry is told what is not covered and what each item needs; covering it passes the gate.</summary>
    [Fact]
    public async Task The_retry_names_the_uncovered_items_and_covering_them_passes_the_gate()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), Read(pages[0]), Read(pages[1]), Notes(pages[..2]), Turn.Says("Reviewed two."),
            Read(pages[2]), Notes(pages), Turn.Says("Reviewed all three.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        var retry = worker.Requests.Select(r => r.Messages.LastOrDefault(m => m.Content?.Contains("coverage check", StringComparison.Ordinal) == true)?.Content)
            .OfType<string>().First();
        Assert.Contains("wiki/p03.md", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("wiki/p01.md", retry, StringComparison.Ordinal);
        Assert.Contains("submit_step_output", retry, StringComparison.Ordinal);
        Assert.Single(GateFailures(events, 2));
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>Asked for whole reads: a result for every item is not enough, and the three not read are named.</summary>
    [Fact]
    public async Task With_file_read_twelve_results_and_three_not_read_is_rejected_naming_the_three()
    {
        var pages = Pages(12);
        using var fx = Wiki(12);
        var worker = new FakeChatProvider([Turn.Says(PlanFor("file_read")), .. Found(pages), .. pages[..9].Select(Read), Notes(pages), Turn.Says("Reviewed all.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.Equal("covered 9 of 12; not covered: wiki/p10.md (never read); wiki/p11.md (never read); wiki/p12.md (never read).", end.OutcomeReason());
    }

    /// <summary>Asked for writes: the pages the step changed count, the one it only read does not.</summary>
    [Fact]
    public async Task With_file_write_a_recorded_write_covers_an_item_and_a_read_does_not()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(PlanFor("file_write")), .. Found(pages),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = pages[0], content = "# fixed\n" }), "w1"),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = pages[1], content = "# fixed\n" }), "w2"),
            Read(pages[2]), Notes(pages), Turn.Says("Fixed them.")]);

        var events = await fx.RunAsync(fx.Build(worker), "fix every wiki page");

        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.Equal("covered 2 of 3; not covered: wiki/p03.md (no file_write evidence).", end.OutcomeReason());
    }

    /// <summary>
    /// The gate and the review count their own failures: a step that fails the gate once and then the review once has
    /// its review retry still to come.
    /// </summary>
    [Fact]
    public async Task A_gate_failure_does_not_use_up_a_review_retry()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), Read(pages[0]), Read(pages[1]), Notes(pages[..2]), Turn.Says("Reviewed two."),
            Read(pages[2]), Notes(pages), Turn.Says("Reviewed all three."),
            Turn.Says("Corrected the note.")]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Fail("the note on p02 is wrong"), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "review every wiki page");

        Assert.Single(GateFailures(events, 2));
        Assert.Equal(2, ReviewsOf(reviewer, 2).Length);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
    }

    /// <summary>A step with nothing to cover is tried and reviewed exactly as before: no gate, the review's budget alone.</summary>
    [Fact]
    public async Task A_step_with_no_coverage_to_check_is_tried_and_reviewed_as_before()
    {
        const string plan = """
            {"disposition":"task","title":"two steps",
             "steps":[{"title":"look around","dependsOn":[]},{"title":"write it up","dependsOn":[0]}]}
            """;
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        var worker = new FakeChatProvider(Turn.Says(plan), Turn.Says("Looked."), Turn.Says("Wrote it."), Turn.Says("Wrote it again."), Turn.Says("And again."));
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Fail(), Verdicts.Fail());

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "look around and write it up");

        Assert.DoesNotContain(events, e => e.Summary.Contains("coverage check", StringComparison.Ordinal));
        Assert.Equal(2, ReviewsOf(reviewer, 2).Length);
        Assert.Equal(StepOutcomeKind.ReviewRejected, StepEnd(events, 2).StepOutcome());
        Assert.StartsWith("review not passed:", StepEnd(events, 2).OutcomeReason(), StringComparison.Ordinal);
    }
}
