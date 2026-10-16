namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Plan Phase 1.3, fan-out: the coverage a step done for each item owes is decided at its join, from what the item steps
/// handed on and the evidence behind each - and never by rejecting the join. What the finished items handed on is kept
/// and handed on, the steps after the join run, and they are told by name which items are not covered, so a report can
/// say what is missing. The join is done, not confirmed; the run-end check still decides the run, which cannot be green.
/// </summary>
public sealed class CoverageAtTheJoinTests
{
    private static string[] Pages(int n) => Enumerable.Range(1, n).Select(i => $"wiki/p{i:00}.md").ToArray();

    private static string PlanFor(string evidence) => """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},
                   "consumes":[{"step":0,"field":"pages","evidence":"EVIDENCE"}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}},
                  {"title":"write the report","dependsOn":[1]}]}
        """.Replace("EVIDENCE", evidence, StringComparison.Ordinal);

    private static EngineFixture Wiki(int pages)
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true, DynamicSteps = true };
        foreach (var page in Pages(pages))
            fx.Write(page, $"# {page}\nabout it\n");
        return fx;
    }

    private static Turn[] Found(string[] pages)
        => [Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { pages }), "s0"), Turn.Says("Found them.")];

    /// <summary>One item's step: read the page (or not), and hand its note on.</summary>
    private static Turn[] Review(string page, bool read = true)
        => [.. read ? [Turn.Calls1("read_file", JsonSerializer.Serialize(new { path = page }), "r-" + page)] : Array.Empty<Turn>(),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes = new Dictionary<string, string> { [page] = "fine" } }), "s-" + page),
            Turn.Says("Reviewed " + page)];

    /// <summary>One item's step that never hands its result on.</summary>
    private static Turn[] NotFinished() => [Turn.Says("I looked at it."), Turn.Says("I really did.")];

    private static WorkEvent StepEnd(List<WorkEvent> events, int step)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == step);

    private static string ReportPrompt(FakeChatProvider worker)
        => string.Join("\n", worker.Requests.Last(r => r.Messages.Any(m => m.Content?.Contains("Proceed with this step of the plan: write the report",
            StringComparison.Ordinal) == true)).Messages.Select(m => m.Content ?? ""));

    /// <summary>
    /// THE ONE THAT MATTERS: two of five items do not finish. The join is not rejected - it is done, not confirmed, with
    /// the count; what the three handed on is handed on; the report step runs and is told which two are not covered;
    /// and the run-end check, not the join, keeps the run from Completed.
    /// </summary>
    [Fact]
    public async Task Two_of_five_items_not_finished_leave_the_join_unconfirmed_and_the_report_names_them()
    {
        var pages = Pages(5);
        using var fx = Wiki(5);
        var worker = new FakeChatProvider([Turn.Says(PlanFor("mentioned")), .. Found(pages),
            .. Review(pages[0]), .. NotFinished(), .. Review(pages[2]), .. NotFinished(), .. Review(pages[4]),
            Turn.Says("Report written: two pages were not reviewed.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page and write a report");

        var join = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, join.StepOutcome());
        Assert.StartsWith("covered 3 of 5; not covered: wiki/p02.md (no result); wiki/p04.md (no result).", join.OutcomeReason(), StringComparison.Ordinal);
        // The partial output is kept and handed on.
        var joined = events.Last(e => e.Kind == EventKind.StepOutputRecorded && e.StepNo() == 2);
        Assert.Contains("wiki/p05.md", joined.Summary, StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 3).StepOutcome());
        var prompt = ReportPrompt(worker);
        Assert.Contains("""{"notes":{"wiki/p01.md":"fine","wiki/p03.md":"fine","wiki/p05.md":"fine"}}""", prompt, StringComparison.Ordinal);
        Assert.Contains("'review page' leaves items not covered, as the coverage the plan requires counts them (covered 3 of 5): "
                        + "wiki/p02.md (no result), wiki/p04.md (no result).", prompt, StringComparison.Ordinal);
        // The run-end check decides the run, and it is not green.
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("FAIL", StringComparison.Ordinal)
                                     && e.Summary.Contains("covered 3 of 5", StringComparison.Ordinal));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>Every item finishes, one without the evidence it owes: the same, naming that item.</summary>
    [Fact]
    public async Task An_item_finished_without_its_evidence_leaves_the_join_unconfirmed_and_is_named()
    {
        var pages = Pages(5);
        using var fx = Wiki(5);
        var worker = new FakeChatProvider([Turn.Says(PlanFor("file_read")), .. Found(pages),
            .. Review(pages[0]), .. Review(pages[1]), .. Review(pages[2], read: false), .. Review(pages[3]), .. Review(pages[4]),
            Turn.Says("Report written.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page and write a report");

        var join = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, join.StepOutcome());
        Assert.StartsWith("covered 4 of 5; not covered: wiki/p03.md (never read).", join.OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 3).StepOutcome());
        Assert.Contains("(covered 4 of 5): wiki/p03.md (never read).", ReportPrompt(worker), StringComparison.Ordinal);
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("FAIL", StringComparison.Ordinal)
                                     && e.Summary.Contains("covered 4 of 5", StringComparison.Ordinal));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    private const string FilteringEach = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},
                   "consumes":[{"step":0,"field":"pages","subset":true}],
                   "output":{"notes":{"type":"results","description":"a note per page"},
                             "excluded":{"type":"results","description":"why a page is left out"}}},
                  {"title":"write the report","dependsOn":[1]}]}
        """;

    /// <summary>
    /// A step done for each item that filters: an item's step may leave its page out with a reason. The partition holds
    /// at the join - but a join has no reviewer, and its item steps are not asked about what they left out, so nothing
    /// approves the omission: the join is done, not confirmed, the exclusion named, and the run cannot be Completed.
    /// </summary>
    [Fact]
    public async Task Items_left_out_by_a_step_for_each_item_leave_the_join_unconfirmed()
    {
        var pages = Pages(3);
        using var fx = Wiki(3);
        var worker = new FakeChatProvider([Turn.Says(FilteringEach), .. Found(pages),
            .. Review(pages[0]),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { excluded = new Dictionary<string, string> { [pages[1]] = "a stub" } }), "x-p02"),
            Turn.Says("Left the stub out."),
            .. Review(pages[2]),
            Turn.Says("Report written.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review the wiki pages worth reviewing and write a report");

        var join = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, join.StepOutcome());
        Assert.Contains($"{pages[1]} (a stub)", join.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("no review approved it", join.OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 3).StepOutcome());
        // The report step is told what was left out, and that nothing approved it - or it could present the stub as done.
        var prompt = ReportPrompt(worker);
        Assert.Contains("'review page' is not confirmed: 1 item(s) were left out, each with a reason: wiki/p02.md (a stub)", prompt, StringComparison.Ordinal);
        Assert.Contains("do not present them as done", prompt, StringComparison.Ordinal);
        // The partition itself holds: the run-end check passes, and still the run is not Completed.
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("Every pages item of step 1", StringComparison.Ordinal));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>A tool that names pages and does nothing with them: a call naming an item is all "mentioned" asks for.</summary>
    private sealed class Tagging : Enactive.Core.Tools.ITool
    {
        public Enactive.Core.Tools.ToolDefinition Definition => new("tag_pages", "tags pages", "{\"type\":\"object\"}", Kind: Enactive.Core.Tools.ToolKind.Read);
        public Enactive.Core.Permissions.PermissionLevel RequiredLevel => Enactive.Core.Permissions.PermissionLevel.Observe;
        public Task<Enactive.Core.Tools.ToolResult> InvokeAsync(string argumentsJson, Enactive.Core.Tools.ToolContext ctx, CancellationToken ct)
            => Task.FromResult(new Enactive.Core.Tools.ToolResult(true, "tagged", null, [], new Dictionary<string, object?>()));
    }

    /// <summary>One item's step that only names its page in a call that succeeds, and hands its note on.</summary>
    private static Turn[] Mention(string page)
        => [Turn.Calls1("tag_pages", JsonSerializer.Serialize(new { paths = new[] { page } }), "t-" + page),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes = new Dictionary<string, string> { [page] = "fine" } }), "s-" + page),
            Turn.Says("Reviewed " + page)];

    private static EngineFixture Tagged(int pages)
    {
        var fx = Wiki(pages);
        fx.ToolsOverride = [.. EngineFixture.ShippedTools(), new Tagging()];
        return fx;
    }

    private sealed class Recording : Enactive.Core.History.IRunCheckpointStore
    {
        public List<Enactive.Core.History.RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(Enactive.Core.History.RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<Enactive.Core.History.RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Enactive.Core.History.RunCheckpoint>>(Saved.ToArray());
        public Task<Enactive.Core.History.RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// THE REVIEW'S PROBE: a step for each of three pages under the default "mentioned", review off, each item step naming
    /// its page in one call that succeeds and handing a note on. The coverage holds - every page named - and shows nothing
    /// done with any of them; an item step has no gate of its own, and the join has no reviewer, so nothing confirms it.
    /// It used to join as Succeeded and the run ended Completed. The join is done, not confirmed, as a step is; the report
    /// step is told which items rest on a mention alone; and the run is not Completed.
    /// </summary>
    [Fact]
    public async Task Items_only_mentioned_with_review_off_leave_the_join_unconfirmed()
    {
        var pages = Pages(3);
        using var fx = Tagged(3);
        var store = new Recording();
        var worker = new FakeChatProvider([Turn.Says(PlanFor("mentioned")), .. Found(pages), .. pages.SelectMany(Mention),
            Turn.Says("Report written.")]);

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file"), checkpoints: store),
            "review every wiki page and write a report");

        var join = StepEnd(events, 2);
        Assert.Equal(StepOutcomeKind.DoneUnverified, join.StepOutcome());
        Assert.Contains("3 item(s) were only mentioned", join.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("no review confirmed it", join.OutcomeReason(), StringComparison.Ordinal);
        var record = store.Saved.Last().Steps.Single(s => s.Title == "review page").Record!;
        Assert.Equal(OutcomeCause.CoverageMentionedOnly, record.Cause);
        // What the items handed on still goes on, and the report is told what rests on a mention alone.
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 3).StepOutcome());
        var prompt = ReportPrompt(worker);
        Assert.Contains("'review page' is not confirmed: 3 item(s) were only mentioned", prompt, StringComparison.Ordinal);
        Assert.Contains("do not present them as done", prompt, StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// The same items, each item step passed by a review: an approving verdict stands behind each mention, as it does for
    /// a step's own (a step is confirmed by its review, mentions and all) - the join stands as done, and the run completes.
    /// </summary>
    [Fact]
    public async Task Items_only_mentioned_stand_at_the_join_when_a_review_passed_each_item_step()
    {
        var pages = Pages(3);
        using var fx = Tagged(3);
        var worker = new FakeChatProvider([Turn.Says(PlanFor("mentioned")), .. Found(pages), .. pages.SelectMany(Mention),
            Turn.Says("Report written.")]);
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass(), Verdicts.Pass(), Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.WorkerWith("tag_pages", "read_file"),
            router: Routers.WithReviewer(), reviewProvider: reviewer), "review every wiki page and write a report");

        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.DoesNotContain("is not confirmed", ReportPrompt(worker), StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>Regression guard: every item covered, the join stands as done as it always did, and the run completes.</summary>
    [Fact]
    public async Task A_fan_out_with_full_coverage_joins_as_done()
    {
        var pages = Pages(5);
        using var fx = Wiki(5);
        var worker = new FakeChatProvider([Turn.Says(PlanFor("file_read")), .. Found(pages), .. pages.SelectMany(p => Review(p)),
            Turn.Says("Report written.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page and write a report");

        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.DoesNotContain("leaves items not covered", ReportPrompt(worker), StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }
}
