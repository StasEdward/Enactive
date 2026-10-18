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
        Assert.All(GateFailures(events, 2), e => Assert.True(e.ByEngineCheck()));
        // How the step ended, and the run, name the engine's check - not a reviewer.
        Assert.True(end.ByEngineCheck());
        Assert.Contains("REJECTED BY THE ENGINE'S COVERAGE CHECK", end.Summary, StringComparison.Ordinal);
        Assert.Contains("[2] review pages - rejected by the engine's coverage check: covered 2 of 12", events.Last().Summary, StringComparison.Ordinal);
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

    // ── "mentioned" is not confirmation ──────────────────────────────────────────────────

    /// <summary>A call that names every page, and does nothing else with them: what "mentioned" accepts.</summary>
    private sealed class Tagging : Enactive.Core.Tools.ITool
    {
        public Enactive.Core.Tools.ToolDefinition Definition => new("tag_pages", "tags pages", "{\"type\":\"object\"}", Kind: Enactive.Core.Tools.ToolKind.Read);
        public Enactive.Core.Permissions.PermissionLevel RequiredLevel => Enactive.Core.Permissions.PermissionLevel.Observe;
        public Task<Enactive.Core.Tools.ToolResult> InvokeAsync(string argumentsJson, Enactive.Core.Tools.ToolContext ctx, CancellationToken ct)
            => Task.FromResult(new Enactive.Core.Tools.ToolResult(true, "tagged", null, [], new Dictionary<string, object?>()));
    }

    private static (EngineFixture Fx, FakeChatProvider Worker) OneCallNamingAll(int pages)
    {
        var fx = Wiki(pages);
        var all = Pages(pages);
        fx.ToolsOverride = [.. EngineFixture.ShippedTools(), new Tagging()];
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(all),
            Turn.Calls1("tag_pages", JsonSerializer.Serialize(new { paths = all }), "t1"), Notes(all), Turn.Says("Reviewed all twelve.")]);
        return (fx, worker);
    }

    /// <summary>
    /// The journal keeps a call's arguments compacted - cut at 120 characters, no longer JSON - and a mention used to be
    /// read from that: a call listing twelve paths named none of them. It is read from the call's full arguments now.
    /// </summary>
    [Fact]
    public async Task One_call_naming_twelve_paths_in_more_than_120_characters_mentions_all_twelve()
    {
        Assert.True(JsonSerializer.Serialize(new { paths = Pages(12) }).Length > 120);
        var (fx, worker) = OneCallNamingAll(12);
        using var _ = fx;

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file")), "review every wiki page");

        Assert.Empty(GateFailures(events, 2));
        var recorded = events.Last(e => e.Kind == EventKind.StepOutputRecorded && e.StepNo() == 2);
        Assert.Equal(12, System.Text.RegularExpressions.Regex.Matches(recorded.PayloadJson!, "\"complete\":\\[\"Call\"\\]").Count);
    }

    /// <summary>
    /// A step that writes each item's file has named each one - its write_file arguments run far past 120 characters -
    /// and under the default "mentioned" it passes the gate, backed by more than a mention.
    /// </summary>
    [Fact]
    public async Task A_step_that_writes_each_items_file_covers_it_under_mentioned()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        Turn Write(string page) => Turn.Calls1("write_file",
            JsonSerializer.Serialize(new { path = page, content = $"# {page}\n" + new string('x', 200) + "\n" }), "w-" + page);
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), .. pages.Select(Write), Notes(pages), Turn.Says("Rewrote all three.")]);

        var events = await fx.RunAsync(fx.Build(worker), "rewrite every wiki page");

        Assert.Empty(GateFailures(events, 2));
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// One call naming all twelve paths covers all twelve under "mentioned" - and shows nothing done to any of them.
    /// With no review to approve it, the step is done but not confirmed, and the passing final checks do not make the
    /// run Completed.
    /// </summary>
    [Fact]
    public async Task One_call_naming_every_item_with_review_off_is_done_but_not_confirmed()
    {
        var (fx, worker) = OneCallNamingAll(12);
        using var _ = fx;

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file")), "review every wiki page");

        Assert.True(GateFailures(events, 2).Length == 0, events.Text());
        var end = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Contains("only mentioned", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("Every pages item of step 1 is covered by step 2", StringComparison.Ordinal));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>With review on, an approving verdict is what lets it stand as done.</summary>
    [Fact]
    public async Task One_call_naming_every_item_stands_as_done_on_an_approving_review()
    {
        var (fx, worker) = OneCallNamingAll(12);
        using var _ = fx;
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "review every wiki page");

        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>A review with no usable verdict approves nothing: done, not confirmed - as any step without one.</summary>
    [Fact]
    public async Task One_call_naming_every_item_without_a_usable_verdict_is_done_but_not_confirmed()
    {
        var (fx, worker) = OneCallNamingAll(12);
        using var _ = fx;
        var reviewer = new FakeChatProvider(Verdicts.Pass()) { WhenExhausted = Turn.Says("no idea") };

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "review every wiki page");

        Assert.Equal(StepOutcomeKind.DoneUnverified, StepEnd(events, 2).StepOutcome());
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// A step that hands on a note for one of two pages and then says it cannot go on, with nothing the engine found behind
    /// its word, is reviewed as a finished step - and a passing review made it Succeeded with the gate never run: one
    /// page of two stood as done. Counted at the gate like any finished step: short, it goes back with what it owes and
    /// that nothing stops it, and no reviewer is asked until it has covered both.
    /// </summary>
    [Fact]
    public async Task A_step_that_says_it_is_blocked_is_counted_at_the_gate_before_its_review()
    {
        var pages = Pages(2);
        using var fx = Wiki(2);
        fx.ReportBlocked = true;
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), Read(pages[0]), Notes(pages[..1]),
            Turn.Calls1(AgentBlocked.ToolName, """{"reason":"the second page cannot be reviewed"}""", "b1"),
            Read(pages[1]), Notes(pages), Turn.Says("Reviewed both.")]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "review every wiki page");

        var gate = Assert.Single(GateFailures(events, 2));
        Assert.Contains("covered 1 of 2", gate.Summary, StringComparison.Ordinal);
        Assert.Contains(worker.Requests.Last().Messages, m => m.Content?.Contains(Orchestrator.NotABlock, StringComparison.Ordinal) == true);
        Assert.Single(ReviewsOf(reviewer, 2));
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>Read, the same pages are more than mentioned: with review off the step stands as done.</summary>
    [Fact]
    public async Task Items_read_and_not_only_mentioned_stand_as_done_with_review_off()
    {
        var pages = Pages(2);
        using var fx = Wiki(2);
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), Read(pages[0]), Read(pages[1]), Notes(pages), Turn.Says("Reviewed both.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── what the reviewer is shown ───────────────────────────────────────────────────────

    /// <summary>
    /// The reviewer is shown, item by item, what the engine found: the items with no more than a mention behind them in
    /// full - what was required, what was found, the calls, the result - and the items backed as required as a count.
    /// The engine says it decided only that the evidence is there; whether the work is what was asked is the reviewer's.
    /// </summary>
    [Fact]
    public async Task The_reviewer_is_shown_each_weakly_backed_item_in_full_and_the_rest_as_a_count()
    {
        var pages = Pages(12);
        using var fx = Wiki(12);
        fx.ToolsOverride = [.. EngineFixture.ShippedTools(), new Tagging()];
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), .. pages[..9].Select(Read),
            .. pages[9..].Select(p => Turn.Calls1("tag_pages", JsonSerializer.Serialize(new { paths = new[] { p } }), "t-" + p)),
            Notes(pages), Turn.Says("Reviewed all twelve.")]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "review every wiki page");

        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        var asked = string.Join("\n", Assert.Single(ReviewsOf(reviewer, 2)).Messages.Select(m => m.Content));
        Assert.Contains(StepCoverageGate.SectionHeading, asked, StringComparison.Ordinal);
        Assert.Contains("whether the work on each item is what was asked is yours to judge", asked, StringComparison.Ordinal);
        foreach (var weak in pages[9..])
            Assert.Contains($"- {weak}: required mentioned; found mentioned; by tag_pages {{\"paths\":[\"{weak}\"]}}; result: {weak} is fine", asked, StringComparison.Ordinal);
        Assert.Contains("Backed as required: 9 of 12 - read whole 9.", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("- wiki/p01.md:", asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// The block reaches the reviewer whole when the step's own calls fill the evidence budget: twelve reads of several
    /// hundred characters each share it out, and an entry among them was cut in the middle - where the weak items are.
    /// </summary>
    [Fact]
    public async Task The_reviewer_gets_the_weak_items_when_the_reads_fill_the_evidence_budget()
    {
        var pages = Pages(12);
        var asked = await ReviewOfTwelveReads(Enactive.Core.Execution.ExecutionJournal.DefaultBudget);

        Assert.Contains("characters not shown here", asked, StringComparison.Ordinal);   // the reads did fill the budget
        foreach (var weak in pages[9..])
            Assert.Contains($"- {weak}: required mentioned; found mentioned; by tag_pages", asked, StringComparison.Ordinal);
        Assert.Contains("Backed as required: 9 of 12 - read whole 9.", asked, StringComparison.Ordinal);
        // Its room comes out of the evidence budget: the account and the calls together keep to it.
        var (account, calls) = AccountAndCalls(asked);
        Assert.True(account.Length + calls.Length <= Enactive.Core.Execution.ExecutionJournal.DefaultBudget + 200, $"{account.Length} + {calls.Length}");
    }

    /// <summary>
    /// A budget too small for both keeps to its total all the same: the account gives way - shortened at 3000, left out at
    /// the journal's minimum of 1500, where the calls need all of it - and the calls are never squeezed below that minimum.
    /// </summary>
    [Theory]
    [InlineData(3000, true)]
    [InlineData(1500, false)]
    public async Task A_small_evidence_budget_is_kept_to_and_the_account_gives_way(int budget, bool shown)
    {
        var asked = await ReviewOfTwelveReads(budget);

        Assert.Equal(shown, asked.Contains(StepCoverageGate.SectionHeading, StringComparison.Ordinal));
        var (account, calls) = AccountAndCalls(asked);
        Assert.True(account.Length + calls.Length <= budget + 200, $"{account.Length} + {calls.Length} > {budget}");
        if (shown) Assert.True(account.Length <= budget / 2, $"{account.Length}");
    }

    private static (string Account, string Calls) AccountAndCalls(string asked)
    {
        var at = asked.IndexOf(StepCoverageGate.SectionHeading, StringComparison.Ordinal);
        var account = at < 0 ? "" : asked[(at + StepCoverageGate.SectionHeading.Length)..asked.IndexOf("FILES this step changed:", StringComparison.Ordinal)].Trim();
        var calls = asked[(asked.IndexOf("TOOL CALLS (cite them by [n]):", StringComparison.Ordinal) + "TOOL CALLS (cite them by [n]):".Length)..].Trim();
        return (account, calls);
    }

    /// <summary>
    /// The short review of a consuming step that read twelve files of about 600 characters each - nine pages and three
    /// background notes - and only tagged the last three pages: the weak items.
    /// </summary>
    private static async Task<string> ReviewOfTwelveReads(int evidenceBudget)
    {
        var pages = Pages(12);
        string[] background = ["notes/style.md", "notes/terms.md", "notes/owners.md"];
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        foreach (var file in pages.Concat(background))
            fx.Write(file, string.Join("\n", Enumerable.Range(1, 12).Select(l => $"{file} line {l}: some text about this page")) + "\n");
        fx.ToolsOverride = [.. EngineFixture.ShippedTools(), new Tagging()];
        var worker = new FakeChatProvider([Turn.Says(PlanFor()), .. Found(pages), .. pages[..9].Concat(background).Select(Read),
            .. pages[9..].Select(p => Turn.Calls1("tag_pages", JsonSerializer.Serialize(new { paths = new[] { p } }), "t-" + p)),
            Notes(pages), Turn.Says("Reviewed all twelve.")]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file"), router: Routers.WithReviewer(), reviewProvider: reviewer,
                evidenceBudget: evidenceBudget),
            "review every wiki page");

        return string.Join("\n", Assert.Single(ReviewsOf(reviewer, 2)).Messages.Select(m => m.Content));
    }

    /// <summary>The block keeps to its share of the evidence budget: what does not fit is named, then counted.</summary>
    [Fact]
    public void The_reviewers_block_fits_its_budget()
    {
        var pages = Enumerable.Range(1, 200).Select(i => $"wiki/a-rather-long-page-name-{i:000}.md").ToArray();
        var gate = StepCoverageGate.Check([Coverage(1)], [Output(1, new { pages }),
            Output(2, new { notes = pages.ToDictionary(p => p, p => new string('x', 300)) }, pages.Select(p => new ItemEvidence("notes", p, [EvidenceKind.Call], null)).ToArray())],
            null, 3)!;
        var actions = pages.Select(p => new Enactive.Core.Execution.ExecutedAction(DateTimeOffset.UtcNow, 2, "tag_pages",
            JsonSerializer.Serialize(new { paths = new[] { p } }), Enactive.Core.Execution.ActionOutcome.Succeeded, "tagged")).ToArray();

        var block = StepCoverageGate.ReviewerBlock(gate, actions, maxChars: 3000);

        Assert.True(block.Length <= 3000, $"{block.Length} characters");
        Assert.Contains($"- {pages[0]}: required mentioned; found mentioned; by tag_pages", block, StringComparison.Ordinal);
        Assert.Contains("more item(s) with no more than a mention behind them", block, StringComparison.Ordinal);
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
