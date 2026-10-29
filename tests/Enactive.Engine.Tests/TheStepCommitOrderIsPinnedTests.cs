namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Plan Phase 4.0: what a step's end commits, and in what order - pinned as it is, so a change to the code that commits
/// it (Orchestrator.RunStepAsync, JoinAsync, ExpandAsync) shows what it moved. The order is load-bearing, and the comments
/// at the commit say why: what a step handed on before MarkDone, or a dependent released a moment early does not have it;
/// its StepCompleted before MarkDone or MarkFailed, or a dependent's StepStarted reaches the reader first; a revert before
/// StepCompleted; the checkpoint after MarkDone, or it records a finished step as Running and a resume redoes it. Owner's
/// decision, 2026-10-29 (open question 4): 4.1 and 4.3 are not done until a concrete failure asks for them; these are.
/// One step at a time: with steps side by side the order between them is the scheduler's, and the digest a dependent
/// with its own conversation reads is pinned by ParallelStepTests. Deliberately not code: invoices.
/// </summary>
public sealed class TheStepCommitOrderIsPinnedTests
{
    private sealed class Store : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) { lock (Saved) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray()); }
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) { lock (Saved) return Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId)); }
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;

        /// <summary>Each checkpoint as the statuses it recorded, step by step: "Done/Succeeded, Pending, Pending".</summary>
        public string[] Statuses() { lock (Saved) return Saved.Select(c => string.Join(", ", c.Steps.Select(s => s.Status + (s.Outcome is { } o ? "/" + o : "")))).ToArray(); }
    }

    /// <summary>The events a commit is made of, in the order the reader received them: kind and step.</summary>
    private static string[] Commits(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind is EventKind.StepStarted or EventKind.StepCompleted or EventKind.StepOutputRecorded
                or EventKind.ArtifactReverted or EventKind.PlanExpanded)
            .Select(e => $"{e.Kind} {e.StepNo()}" + (e.Kind == EventKind.StepCompleted ? " " + e.StepOutcome() : "")).ToArray();

    /// <summary>The commit as it is: its events in order, and each checkpoint's statuses - whole, so any move shows.</summary>
    private static void Pinned(IEnumerable<WorkEvent> events, Store store, string[] commits, string[] checkpoints)
    {
        Assert.Equal(commits, Commits(events));
        Assert.Equal(checkpoints, store.Statuses());
    }

    private const string ThreeSteps = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Add up the invoices","dependsOn":[]},
                  {"title":"Write the summary","dependsOn":[0]},
                  {"title":"Say it is ready","dependsOn":[1]}]}
        """;

    private const string WritesThenWaits = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Write the totals","dependsOn":[]},
                  {"title":"Say it is ready","dependsOn":[0]}]}
        """;

    [Fact]
    public async Task Done_steps_say_they_are_done_before_the_next_starts_and_their_checkpoint_says_done()
    {
        using var fx = new EngineFixture();
        var store = new Store();
        var worker = new ByStepChatProvider(ThreeSteps)
            .Step("Add up the invoices", Turn.Says("The invoices add up to 10."))
            .Step("Write the summary", Turn.Says("Summary: 10."))
            .Step("Say it is ready", Turn.Says("Ready."));

        var events = await fx.RunAsync(fx.Build(worker, checkpoints: store), "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        // The checkpoint after a step records it Done, never Running: written before MarkDone, a resume redid it.
        Pinned(events, store,
            ["StepStarted 1", "StepCompleted 1 Succeeded", "StepStarted 2", "StepCompleted 2 Succeeded", "StepStarted 3", "StepCompleted 3 Succeeded"],
            ["Pending, Pending, Pending", "Done/Succeeded, Pending, Pending", "Done/Succeeded, Done/Succeeded, Pending",
             "Done/Succeeded, Done/Succeeded, Done/Succeeded"]);
    }

    [Fact]
    public async Task A_rejected_step_is_put_back_before_it_says_so_and_what_waits_on_it_is_skipped_after()
    {
        using var fx = new EngineFixture();
        var store = new Store();
        var worker = new FakeChatProvider(
            Turn.Says(WritesThenWaits),
            Turn.Calls1("write_file", """{"path":"totals.txt","content":"Total: 11"}""", "w1"),
            Turn.Says("Wrote the totals."));
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: new FakeChatProvider(Verdicts.Fail("the total is 10, not 11")), reviewRetries: 0, checkpoints: store),
            "write the totals");

        // Put back before it is said to be rejected, and only then is what waits on it skipped - MarkFailed's cascade.
        Pinned(events, store,
            ["StepStarted 1", "ArtifactReverted 1", "StepCompleted 1 ReviewRejected", "StepCompleted 2 Skipped"],
            ["Pending, Pending", "Failed/ReviewRejected, Skipped/Skipped"]);
        Assert.False(fx.Exists("totals.txt"));
    }

    [Fact]
    public async Task A_step_done_but_not_verified_releases_what_waits_on_it_as_a_done_one_does()
    {
        using var fx = new EngineFixture();
        var store = new Store();
        var worker = new FakeChatProvider(
            Turn.Says(WritesThenWaits),
            Turn.Calls1("write_file", """{"path":"totals.txt","content":"Total: 10"}""", "w1"),
            Turn.Says("Wrote the totals."),
            Turn.Says("Ready."));
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: new FakeChatProvider(Turn.Says("this is not a verdict"), Turn.Says("nor is this"), Verdicts.Pass()),
            checkpoints: store), "write the totals");

        // Done, not verified, releases its dependent as a done step does: said first, then the next one starts.
        Pinned(events, store,
            ["StepStarted 1", "StepCompleted 1 DoneUnverified", "StepStarted 2", "StepCompleted 2 Succeeded"],
            ["Pending, Pending", "Done/DoneUnverified, Pending", "Done/DoneUnverified, Done/Succeeded"]);
    }

    [Fact]
    public async Task A_blocked_step_says_so_then_what_waits_on_it_then_the_checkpoint()
    {
        using var fx = new EngineFixture();
        var store = new Store();
        fx.Decisions.Answer = "deny";
        var worker = new ByStepChatProvider(ThreeSteps)
            .Step("Add up the invoices", Turn.Says("The invoices add up to 10."))
            .Step("Write the summary", Turn.Calls1("write_file", """{"path":"summary.md","content":"Total: 10"}""", "w1"),
                Turn.Says("I could not write it."));
        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: new PermissionPolicy(PermissionLevel.Execute, ["*"], ["write_file"]), checkpoints: store),
            "add up the invoices and write a summary");

        // Blocked, then what waits on it held with it, then one checkpoint with both - not skipped: nothing it needs failed.
        Pinned(events, store,
            ["StepStarted 1", "StepCompleted 1 Succeeded", "StepStarted 2", "StepCompleted 2 Blocked", "StepCompleted 3 Blocked"],
            ["Pending, Pending, Pending", "Done/Succeeded, Pending, Pending", "Done/Succeeded, Blocked/Blocked, Blocked/Blocked"]);
    }

    private const string FindReviewSummarise = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},
                   "output":{"notes":{"type":"results","description":"a note per page"}}},
                  {"title":"summarise","dependsOn":[1]}]}
        """;

    [Fact]
    public async Task Items_are_expanded_then_checkpointed_and_the_join_says_it_is_done_before_its_checkpoint()
    {
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\nabout a\n");
        fx.Write("wiki/b.md", "# B\nabout b\n");
        var store = new Store();
        Turn[] Review(string page, string id) => [
            Turn.Calls1("read_file", $$"""{"path":"{{page}}"}""", "r" + id),
            Turn.Calls1(StepOutputContract.ToolName, $$$"""{"notes":{"{{{page}}}":"fine"}}""", "s" + id),
            Turn.Says("Reviewed " + page)];
        var worker = new FakeChatProvider([Turn.Says(FindReviewSummarise),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s0"), Turn.Says("Found two."),
            .. Review("wiki/a.md", "a"), .. Review("wiki/b.md", "b"), Turn.Says("Summarised both.")]);
        var events = await fx.RunAsync(fx.Build(worker, checkpoints: store), "review every wiki page");

        // The step's output recorded before it is said done; the plan grown and checkpointed with its item steps before any
        // runs; the join (step 2) said done after its items and before its checkpoint; the step after it last.
        Pinned(events, store,
            ["StepStarted 1", "StepOutputRecorded 1", "StepCompleted 1 Succeeded", "PlanExpanded 2",
             "StepStarted 4", "StepOutputRecorded 4", "StepCompleted 4 Succeeded",
             "StepStarted 5", "StepOutputRecorded 5", "StepCompleted 5 Succeeded",
             "StepStarted 2", "StepOutputRecorded 2", "StepCompleted 2 Succeeded", "StepStarted 3", "StepCompleted 3 Succeeded"],
            ["Pending, Pending, Pending", "Done/Succeeded, Pending, Pending", "Done/Succeeded, Pending, Pending, Pending, Pending",
             "Done/Succeeded, Pending, Pending, Done/Succeeded, Pending", "Done/Succeeded, Pending, Pending, Done/Succeeded, Done/Succeeded",
             "Done/Succeeded, Done/Succeeded, Pending, Done/Succeeded, Done/Succeeded",
             "Done/Succeeded, Done/Succeeded, Done/Succeeded, Done/Succeeded, Done/Succeeded"]);
    }
}
