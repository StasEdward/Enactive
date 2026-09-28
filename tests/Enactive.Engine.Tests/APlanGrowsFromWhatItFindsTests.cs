namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// Phase 5.3-5.4: a step declared "for each" item an earlier step hands on is given one step per item
/// once the list exists, and joins what they hand on; past the limits the engine asks, and a graph that
/// grew is carried through a restart.
/// </summary>
public sealed class APlanGrowsFromWhatItFindsTests
{
    // ── the scheduler ────────────────────────────────────────────────────────────────────

    private static (DagScheduler Scheduler, PlanStep Find, PlanStep Each, PlanStep After) Graph()
    {
        var find = new PlanStep(Guid.NewGuid(), "find", StepStatus.Pending, []);
        var each = new PlanStep(Guid.NewGuid(), "review", StepStatus.Pending, [find.Id]) { ForEach = new ForEachSource(0, "pages") };
        var after = new PlanStep(Guid.NewGuid(), "summarise", StepStatus.Pending, [each.Id]);
        return (new DagScheduler(new Plan(Guid.NewGuid(), [find, each, after])), find, each, after);
    }

    private static PlanStep Item(PlanStep each, string item)
        => new(Guid.NewGuid(), $"review: {item}", StepStatus.Pending, each.DependsOn) { Items = [item] };

    [Fact]
    public void A_step_for_each_item_is_handed_out_after_its_source_and_then_waits_for_its_items()
    {
        var (scheduler, find, each, after) = Graph();
        Assert.Equal(find.Id, scheduler.NextReady()!.Id);
        scheduler.MarkDone(find.Id);

        Assert.Equal(each.Id, scheduler.NextReady()!.Id);                       // handed out to be expanded
        var a = Item(each, "a"); var b = Item(each, "b");
        scheduler.Expand(each.Id, [a, b]);

        Assert.Equal(5, scheduler.Total);                                        // the three planned, and one per item
        Assert.Equal([a.Id, b.Id], scheduler.NextReadyBatch(5).Select(s => s.Id).ToArray());   // not the join, not what follows it
        scheduler.MarkDone(a.Id);
        Assert.Null(scheduler.NextReady());
        scheduler.MarkDone(b.Id);
        Assert.Equal(each.Id, scheduler.NextReady()!.Id);                       // the join
        Assert.True(scheduler.Steps.Single(s => s.Id == each.Id).Joins);
        scheduler.MarkDone(each.Id);
        Assert.Equal(after.Id, scheduler.NextReady()!.Id);
    }

    /// <summary>Amendment D: one item that did not finish does not take the other items' results with it.</summary>
    [Fact]
    public void An_item_that_failed_does_not_hold_the_join_back_and_all_of_them_failing_does()
    {
        var (scheduler, find, each, after) = Graph();
        scheduler.MarkDone(find.Id);
        scheduler.NextReady();
        var a = Item(each, "a"); var b = Item(each, "b");
        scheduler.Expand(each.Id, [a, b]);
        scheduler.NextReadyBatch(2);

        Assert.Empty(scheduler.MarkFailed(a.Id));                                // nothing skipped: b is still going
        scheduler.MarkDone(b.Id);
        Assert.Equal(each.Id, scheduler.NextReady()!.Id);

        var (again, find2, each2, after2) = Graph();
        again.MarkDone(find2.Id);
        again.NextReady();
        var c = Item(each2, "c");
        again.Expand(each2.Id, [c]);
        again.NextReady();
        var skipped = again.MarkFailed(c.Id);
        Assert.Equal([each2.Id, after2.Id], skipped.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void A_step_given_no_items_is_ready_to_join_at_once()
    {
        var (scheduler, find, each, _) = Graph();
        scheduler.MarkDone(find.Id);
        scheduler.NextReady();
        scheduler.Expand(each.Id, [], "nothing allowed");
        Assert.Equal(each.Id, scheduler.NextReady()!.Id);
    }

    // ── the plan and the pieces ──────────────────────────────────────────────────────────

    private static StepOutputSchema List(string field, StepOutputFieldType type = StepOutputFieldType.PathList)
        => new("s", 1, [new StepOutputField(field, type, field)]);

    [Fact]
    public void A_step_for_each_item_waits_for_its_source_and_one_that_cannot_be_honoured_runs_once()
    {
        var find = new PlanStep(Guid.NewGuid(), "find", StepStatus.Pending, []) { Output = List("pages") };
        var good = new PlanStep(Guid.NewGuid(), "review", StepStatus.Pending, []) { ForEach = new ForEachSource(0, "pages") };
        var bad = new PlanStep(Guid.NewGuid(), "count", StepStatus.Pending, [find.Id]) { ForEach = new ForEachSource(0, "words") };

        var (plan, dropped) = FanOut.Validate(new Plan(Guid.NewGuid(), [find, good, bad]));

        Assert.Contains(find.Id, plan.Steps[1].DependsOn);                      // added: it cannot run before its items exist
        Assert.NotNull(plan.Steps[1].ForEach);
        Assert.Null(plan.Steps[2].ForEach);
        Assert.Contains("hands on no list 'words'", Assert.Single(dropped));
    }

    [Fact]
    public void Items_are_grouped_as_evenly_as_they_go_and_in_order()
        => Assert.Equal([["a", "b", "c"], ["d", "e"]],
            FanOut.Batches(["a", "b", "c", "d", "e"], 2).Select(g => g.ToArray()).ToArray());

    [Fact]
    public void The_join_hands_on_its_items_results_as_one_with_what_backed_them()
    {
        var schema = new StepOutputSchema("s", 1, [new StepOutputField("notes", StepOutputFieldType.Results, "n"),
            new StepOutputField("seen", StepOutputFieldType.PathList, "p"), new StepOutputField("count", StepOutputFieldType.Integer, "c")]);
        var join = new PlanStep(Guid.NewGuid(), "review", StepStatus.Pending, []) { Output = schema };
        StepOutput Out(string item, int count) => new(3, "x", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(new Dictionary<string, object> { ["notes"] = new Dictionary<string, string> { [item] = "ok" }, ["seen"] = new[] { item }, ["count"] = count }),
            [], 1, []) { Items = [new ItemEvidence("notes", item, [EvidenceKind.FileRead], null)] };

        var joined = FanOut.Join(join, 2, [(join, Out("a.md", 1)), (join, Out("b.md", 2))])!;

        using var values = JsonDocument.Parse(joined.ValuesJson);
        Assert.Equal(["a.md", "b.md"], values.RootElement.GetProperty("notes").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(2, values.RootElement.GetProperty("seen").GetArrayLength());
        Assert.False(values.RootElement.TryGetProperty("count", out _));        // a count cannot be joined without deciding what it means
        Assert.Equal(2, joined.StepNo);
        Assert.Equal(2, joined.Items!.Count);
    }

    [Fact]
    public void The_planner_is_told_about_steps_for_each_item_only_when_it_can_use_them()
    {
        Assert.DoesNotContain("forEach", Planner.SystemPromptFor(null, stepOutputs: true), StringComparison.Ordinal);
        Assert.DoesNotContain("forEach", Planner.SystemPromptFor(null, dynamicSteps: true), StringComparison.Ordinal);
        Assert.Contains("forEach", Planner.SystemPromptFor(null, stepOutputs: true, dynamicSteps: true), StringComparison.Ordinal);
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    private const string FindReviewSummarise = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},
                   "output":{"notes":{"type":"results","description":"a note per page"}}},
                  {"title":"summarise","dependsOn":[1]}],
         "criteria":[{"kind":"covers_all","source":{"step":0,"field":"pages"},"results":{"step":1,"field":"notes"},"evidence":"file_read"}]}
        """;

    private static EngineFixture Wiki()
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\nabout a\n");
        fx.Write("wiki/b.md", "# B\nabout b\n");
        return fx;
    }

    private static Turn[] Find => [
        Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s0"), Turn.Says("Found two.")];

    private static Turn[] Review(string page, string id) => [
        Turn.Calls1("read_file", $$"""{"path":"{{page}}"}""", "r" + id),
        Turn.Calls1(StepOutputContract.ToolName, $$$"""{"notes":{"{{{page}}}":"fine"}}""", "s" + id),
        Turn.Says("Reviewed " + page)];

    private static string Conversation(FakeChatProvider provider, string containing)
        => string.Join("\n", provider.Requests.Select(r => string.Join("\n", r.Messages.Select(m => m.Content ?? "")))
            .Last(text => text.Contains(containing, StringComparison.Ordinal)));

    /// <summary>
    /// THE ONE THAT MATTERS: two pages found, two steps made, one per page; the step after them
    /// receives both notes; every page is covered by a whole read; the run is finished.
    /// </summary>
    [Fact]
    public async Task Each_item_found_is_given_its_own_step_and_their_results_are_handed_on_together()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider([Turn.Says(FindReviewSummarise), .. Find,
            .. Review("wiki/a.md", "a"), .. Review("wiki/b.md", "b"), Turn.Says("Summarised both.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var grown = Assert.Single(events, e => e.Kind == EventKind.PlanExpanded);
        Assert.Equal(["review page: wiki/a.md", "review page: wiki/b.md"], grown.PlanSteps());
        Assert.Contains("This step is for one item from step 1's pages: wiki/a.md", Conversation(worker, "review page: wiki/a.md"),
            StringComparison.Ordinal);
        Assert.Contains("""{"notes":{"wiki/a.md":"fine","wiki/b.md":"fine"}}""", Conversation(worker, "Proceed with this step of the plan: summarise"),
            StringComparison.Ordinal);
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("Every pages item", StringComparison.Ordinal));
    }

    /// <summary>Amendment D through a run: a page whose step did not finish is named; the other is handed on and summarised all the same.</summary>
    [Fact]
    public async Task An_item_step_that_does_not_finish_is_named_and_the_rest_go_on()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider([Turn.Says(FindReviewSummarise), .. Find,
            Turn.Says("I looked at it."), Turn.Says("I really did."),                   // page a: never hands its result on
            .. Review("wiki/b.md", "b"), Turn.Says("Summarised what there is.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("review page — DONE, NOT VERIFIED: 1 of 2", StringComparison.Ordinal));
        Assert.Contains("Proceed with this step of the plan: summarise", string.Join("\n", worker.Requests.Last().Messages.Select(m => m.Content ?? "")),
            StringComparison.Ordinal);
        Assert.Contains(events, e => e.IsCheck() && e.Summary.Contains("wiki/a.md (no result)", StringComparison.Ordinal));
    }

    /// <summary>5.4: past the limit the engine asks, and grouping keeps every item in fewer steps.</summary>
    [Fact]
    public async Task Past_the_limit_the_person_is_asked_and_grouping_keeps_every_item()
    {
        using var fx = Wiki();
        fx.FanOut = new FanOutLimits(MaxStepsPerExpansion: 1);
        fx.Decisions.Answer = "batch";
        var worker = new FakeChatProvider([Turn.Says(FindReviewSummarise), .. Find,
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "ra"), Turn.Calls1("read_file", """{"path":"wiki/b.md"}""", "rb"),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/a.md":"fine","wiki/b.md":"fine"}}""", "sab"),
            Turn.Says("Reviewed both."), Turn.Says("Summarised both.")]);

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var question = Assert.Single(fx.Decisions.Requests);
        Assert.Equal("batch", question.RecommendedOptionId);
        Assert.Equal(["review page: wiki/a.md and 1 more"], Assert.Single(events, e => e.Kind == EventKind.PlanExpanded).PlanSteps());
    }

    /// <summary>5.4: with nobody to ask, a fan-out past the limit is not created - the part is left undone, and says so.</summary>
    [Fact]
    public async Task Past_the_limit_with_nobody_to_ask_nothing_is_created()
    {
        using var fx = Wiki();
        fx.FanOut = new FanOutLimits(MaxStepsPerExpansion: 1);
        var worker = new FakeChatProvider([Turn.Says(FindReviewSummarise), .. Find]);

        var events = await fx.RunAsync(fx.Build(worker, decisions: new UnattendedDecisionHandler()), "review every wiki page");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(3, worker.Requests.Count);                                  // the plan and the finding: no item step, no summary
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("review page — INCOMPLETE: 2 items were not given steps", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("summarise — skipped", StringComparison.Ordinal));
    }

    /// <summary>A graph that grew is written down as grown: a restart carries on with the item steps, and does not redo a finished one.</summary>
    [Fact]
    public async Task A_grown_plan_is_carried_through_a_restart()
    {
        using var fx = Wiki();
        var store = new Recording();
        await fx.RunAsync(fx.Build(new FakeChatProvider([Turn.Says(FindReviewSummarise), .. Find,
            .. Review("wiki/a.md", "a"), .. Review("wiki/b.md", "b"), Turn.Says("Summarised both.")]), checkpoints: store), "review every wiki page");

        // Written after page a's step finished and before page b's did.
        var afterA = JsonSerializer.Deserialize<RunCheckpoint>(JsonSerializer.Serialize(
            store.Saved.First(c => c.Steps.Any(s => s.Items is ["wiki/a.md"] && s.Status == "Done"))))!;
        Assert.Equal(5, afterA.Steps.Count);
        Assert.True(afterA.Steps[1].Joins);

        var resumed = new FakeChatProvider([.. Review("wiki/b.md", "b"), Turn.Says("Summarised both.")]);
        var events = await fx.ResumeAsync(fx.Build(resumed, checkpoints: store), afterA);

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        // The restored conversation remembers page a's step; the run does not do it again.
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted && e.Summary.Contains("review page: wiki/a.md", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.StepStarted && e.Summary.Contains("review page: wiki/b.md", StringComparison.Ordinal));
        Assert.Contains("""{"notes":{"wiki/a.md":"fine","wiki/b.md":"fine"}}""", Conversation(resumed, "Proceed with this step of the plan: summarise"),
            StringComparison.Ordinal);
    }

    private sealed class Recording : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }
}
