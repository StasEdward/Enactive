namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Phase 7: a step stopped by something the run cannot remove itself - a permission refused, an input that is not
/// there, a step it waits on, or the step's own advisory report - is BLOCKED: not done, not failed. The engine finds
/// the blocks it can see without being told; the run keeps its checkpoint and what it was given; and once the cause is
/// put right the SAME run carries on from the blocked steps, which are told what stopped them before.
/// Deliberately not code: invoices, their total and a summary.
/// </summary>
public sealed class ABlockedRunCarriesOnTests
{
    private const string Plan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Add up the invoices","dependsOn":[]},
                  {"title":"Write the summary","dependsOn":[0]},
                  {"title":"Say it is ready","dependsOn":[1]}]}
        """;

    private sealed class Store : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) { lock (Saved) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray()); }
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) { lock (Saved) return Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId)); }
        public Task DeleteAsync(Guid runId, CancellationToken ct) { lock (Deleted) Deleted.Add(runId); return Task.CompletedTask; }
    }

    private static PermissionPolicy AskBeforeWriting() => new(PermissionLevel.Execute, ["*"], ["write_file"]);

    private static IEnumerable<WorkEvent> StepsDone(IEnumerable<WorkEvent> events) => events.Where(e => e.Kind == EventKind.StepCompleted);

    /// <summary>THE ONE THAT MATTERS: refused, blocked, kept - then given, and the same run finishes.</summary>
    [Fact]
    public async Task A_refused_permission_blocks_the_step_and_what_waits_on_it_and_the_run_carries_on_once_given()
    {
        using var fx = new EngineFixture();
        var store = new Store();
        fx.Decisions.Answer = "deny";
        var first = new ByStepChatProvider(Plan)
            .Step("Add up the invoices", Turn.Says("The invoices add up to 10."))
            .Step("Write the summary", Turn.Calls1("write_file", """{"path":"summary.md","content":"Total: 10"}""", "w1"),
                Turn.Says("I could not write it."));

        var blocked = await fx.RunAsync(fx.Build(first, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: AskBeforeWriting(), checkpoints: store), "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Blocked, blocked.Last().Outcome());
        Assert.StartsWith("Blocked - [2] Write the summary: needs a permission it was refused", blocked.Last().OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("Put that right and resume this run", blocked.Last().OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Blocked, Assert.Single(StepsDone(blocked), e => e.Summary.Contains("Write the summary")).StepOutcome());
        var waiting = Assert.Single(StepsDone(blocked), e => e.Summary.Contains("Say it is ready"));
        Assert.Equal(StepOutcomeKind.Blocked, waiting.StepOutcome());                   // blocked with it, not skipped
        Assert.Contains("waits for step 2, which is blocked", waiting.Summary, StringComparison.Ordinal);
        Assert.Empty(store.Deleted);                                                      // it has not ended: it waits
        var kept = store.Saved.Last();
        Assert.True(kept.IsResumable);
        var history = Assert.Single(kept.Steps.Single(s => s.Title == "Write the summary").Blocks!);
        Assert.Equal(OutcomeCause.BlockedPermission, history.Cause);
        Assert.False(fx.Exists("summary.md"));

        // Put right: the write is allowed now. The same run carries on - the total is not added up again.
        fx.Decisions.Answer = "allow";
        var second = new ByStepChatProvider(Plan) { Resumed = true }
            .Step("Write the summary", Turn.Calls1("write_file", """{"path":"summary.md","content":"Total: 10"}""", "w2"),
                Turn.Says("Summary written."))
            .Step("Say it is ready", Turn.Says("Ready."));
        var carried = await fx.ResumeAsync(fx.Build(second, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: AskBeforeWriting(), checkpoints: store), kept);

        Assert.Equal(RunOutcomeKind.Completed, carried.Last().Outcome());
        Assert.True(fx.Exists("summary.md"));
        Assert.Empty(second.RequestsFor("Add up the invoices"));
        var told = string.Join("\n", second.RequestsFor("Write the summary")[0].Messages.Select(m => m.Content));
        Assert.Contains("This step was BLOCKED earlier in this run", told, StringComparison.Ordinal);
        Assert.Contains("needs a permission it was refused", told, StringComparison.Ordinal);
        // Waiting behind another step is not something to check again: that step is done now. (One step at a time
        // the steps share a conversation, so step 2's notice is in step 3's history - once, and none of its own.)
        Assert.Single(second.RequestsFor("Say it is ready")[0].Messages,
            m => m.Content?.StartsWith("This step was BLOCKED earlier", StringComparison.Ordinal) == true);
    }

    /// <summary>An input that is not there: the step is blocked on it, named - and goes on once it is there.</summary>
    [Fact]
    public async Task A_missing_input_blocks_the_step_and_it_carries_on_once_the_input_is_there()
    {
        using var fx = new EngineFixture();
        var store = new Store();
        var first = new ByStepChatProvider(Plan)
            .Step("Add up the invoices", Turn.Calls1("read_file", """{"path":"invoices/march.txt"}""", "r1"),
                Turn.Says("There is no March invoice."));

        var blocked = await fx.RunAsync(fx.Build(first, checkpoints: store), "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Blocked, blocked.Last().Outcome());
        var step = Assert.Single(StepsDone(blocked), e => e.Summary.Contains("Add up the invoices"));
        Assert.Contains("none of what the step looked for is there", step.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("invoices/march.txt", step.OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(OutcomeCause.BlockedInput, store.Saved.Last().Steps[0].Blocks![0].Cause);

        fx.Write("invoices/march.txt", "total: 10");
        var second = new ByStepChatProvider(Plan) { Resumed = true }
            .Step("Add up the invoices", Turn.Calls1("read_file", """{"path":"invoices/march.txt"}""", "r2"), Turn.Says("They add up to 10."))
            .Step("Write the summary", Turn.Says("Summary: 10."))
            .Step("Say it is ready", Turn.Says("Ready."));
        var carried = await fx.ResumeAsync(fx.Build(second, checkpoints: store), store.Saved.Last());

        Assert.Equal(RunOutcomeKind.Completed, carried.Last().Outcome());
    }

    /// <summary>The step's own word (7.2): recorded as its word, the rest of its turn not carried out.</summary>
    [Fact]
    public async Task A_step_may_say_it_is_blocked_and_is_taken_at_its_word_as_its_word()
    {
        using var fx = new EngineFixture { ReportBlocked = true };
        var worker = new ByStepChatProvider(Plan)
            .Step("Add up the invoices", Turn.Says("10."))
            .Step("Write the summary", new Turn(null,
            [
                new Enactive.Core.Tools.ToolCall("b1", AgentBlocked.ToolName, """{"reason":"the invoices are in two currencies and the request names none","needs":"which currency the total is in"}"""),
                new Enactive.Core.Tools.ToolCall("w1", "write_file", """{"path":"summary.md","content":"guessed"}""")
            ]));

        var events = await fx.RunAsync(fx.Build(worker), "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Blocked, events.Last().Outcome());
        var step = Assert.Single(StepsDone(events), e => e.Summary.Contains("Write the summary"));
        Assert.Equal("the step reports it cannot go on: the invoices are in two currencies and the request names none "
                     + "(needs: which currency the total is in)", step.OutcomeReason());
        Assert.False(fx.Exists("summary.md"));                                          // nothing after the report is done
        Assert.Contains(AgentBlocked.ToolName, worker.RequestsFor("Write the summary")[0].Tools!.Select(t => t.Name));
    }

    // ── a report with nothing the engine found behind it is reviewed ────────────────────

    /// <summary>The step does its part, then says it is blocked on what the NEXT step is for - as on 2026-10-08.</summary>
    private static ByStepChatProvider DoneThenSaysBlocked() => new ByStepChatProvider(Plan)
        .Step("Add up the invoices", Turn.Says("10."))
        .Step("Write the summary", Turn.Calls1("write_file", """{"path":"summary.md","content":"Total: 10"}""", "w1"),
            new Turn("Summary written.", [new Enactive.Core.Tools.ToolCall("b1", AgentBlocked.ToolName,
                """{"reason":"the summary is written; saying it is ready is the next step's, which this step may not do"}""")]))
        .Step("Say it is ready", Turn.Says("Ready."));

    /// <summary>
    /// A step that did its part and then reported itself blocked on another step's work is reviewed like any finished
    /// step: passed, it is done, its report stays as a note, and the run goes on. On 2026-10-08 a read-only step wrote
    /// its whole analysis, said "the next step requires writing tests", and the run ended BLOCKED with the work done.
    /// </summary>
    [Fact]
    public async Task A_step_that_says_it_is_blocked_after_doing_its_part_is_reviewed_and_goes_on()
    {
        using var fx = new EngineFixture { ReportBlocked = true };
        var worker = DoneThenSaysBlocked();
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            router: Routers.WithReviewer(), reviewProvider: reviewer), "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(StepOutcomeKind.Succeeded, Assert.Single(StepsDone(events), e => e.Summary.Contains("Write the summary")).StepOutcome());
        Assert.NotEmpty(worker.RequestsFor("Say it is ready"));
        Assert.True(fx.Exists("summary.md"));
        Assert.Contains(reviewer.Requests.SelectMany(r => r.Messages),
            m => m.Content?.Contains("The step reported that it cannot go on", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Failed by the review, it is an attempt like any other: tried again with what the review said, and told nothing
    /// blocks it. It used to end BLOCKED, untried: on 2026-10-09 (run 4a5d74) a read-only step put its findings in
    /// report_blocked, the review found one wrong, and the whole run ended Blocked with nothing for a person to remove.
    /// </summary>
    [Fact]
    public async Task A_step_that_says_it_is_blocked_and_is_not_done_is_tried_again()
    {
        using var fx = new EngineFixture { ReportBlocked = true };
        var worker = DoneThenSaysBlocked();
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Fail("the summary does not say what it adds up"), Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            router: Routers.WithReviewer(), reviewProvider: reviewer), "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(StepOutcomeKind.Succeeded, Assert.Single(StepsDone(events), e => e.Summary.Contains("Write the summary")).StepOutcome());
        var retry = worker.RequestsFor("Write the summary");
        Assert.Equal(3, retry.Count);                                     // its two turns, and the attempt after the review
        Assert.Contains(retry[^1].Messages, m => m.Content?.Contains("Nothing stops this step", StringComparison.Ordinal) == true);
        Assert.NotEmpty(worker.RequestsFor("Say it is ready"));
    }

    /// <summary>Failed on every attempt, it is rejected by the review - not blocked: there is nothing for a person to remove.</summary>
    [Fact]
    public async Task A_step_that_says_it_is_blocked_and_is_never_done_is_rejected_not_blocked()
    {
        using var fx = new EngineFixture { ReportBlocked = true };
        var worker = DoneThenSaysBlocked();
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Fail("the summary does not say what it adds up"),
            Verdicts.Fail("still not said"));

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            router: Routers.WithReviewer(), reviewProvider: reviewer), "add up the invoices and write a summary");

        Assert.NotEqual(RunOutcomeKind.Blocked, events.Last().Outcome());
        Assert.Equal(StepOutcomeKind.ReviewRejected, Assert.Single(StepsDone(events), e => e.Summary.Contains("Write the summary")).StepOutcome());
    }

    /// <summary>The refusal of a change in a read-only step says it does not stop the step - it was taken for a stop.</summary>
    [Fact]
    public void A_read_only_refusal_says_it_is_not_a_block()
        => Assert.Contains("This does not stop the step and is not a block", WriteBoundary.ReadOnlyRefusal, StringComparison.Ordinal);

    /// <summary>A block the engine found itself is not put to the review: it is a fact, not the step's word.</summary>
    [Fact]
    public async Task A_block_the_engine_found_is_not_reviewed()
    {
        using var fx = new EngineFixture { ReportBlocked = true };
        fx.Decisions.Answer = "deny";
        var worker = new ByStepChatProvider(Plan)
            .Step("Add up the invoices", Turn.Says("10."))
            .Step("Write the summary", Turn.Calls1("write_file", """{"path":"summary.md","content":"Total: 10"}""", "w1"),
                Turn.Calls1(AgentBlocked.ToolName, """{"reason":"I may not write the file"}""", "b1"));
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: AskBeforeWriting(), router: Routers.WithReviewer(), reviewProvider: reviewer), "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Blocked, events.Last().Outcome());
        Assert.DoesNotContain(reviewer.Requests.SelectMany(r => r.Messages),
            m => m.Content?.Contains("The step reported that it cannot go on", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Without_the_switch_the_step_is_not_offered_the_report()
    {
        using var fx = new EngineFixture();
        var worker = new ByStepChatProvider(Plan);
        await fx.RunAsync(fx.Build(worker), "add up the invoices and write a summary");
        Assert.DoesNotContain(worker.Requests.SelectMany(r => r.Tools ?? []), t => t.Name == AgentBlocked.ToolName);
    }

    /// <summary>Advisory: where the engine sees the cause itself, that is what is recorded, with the step's word beside it.</summary>
    [Fact]
    public async Task What_the_engine_finds_is_the_cause_and_the_steps_report_is_beside_it()
    {
        using var fx = new EngineFixture { ReportBlocked = true };
        fx.Decisions.Answer = "deny";
        var worker = new ByStepChatProvider(Plan)
            .Step("Add up the invoices", Turn.Says("10."))
            .Step("Write the summary", Turn.Calls1("write_file", """{"path":"summary.md","content":"Total: 10"}""", "w1"),
                Turn.Calls1(AgentBlocked.ToolName, """{"reason":"I may not write the file"}""", "b1"));

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: AskBeforeWriting()), "add up the invoices and write a summary");

        var reason = Assert.Single(StepsDone(events), e => e.Summary.Contains("Write the summary")).OutcomeReason()!;
        Assert.StartsWith("needs a permission it was refused", reason, StringComparison.Ordinal);
        Assert.EndsWith("the step reports it cannot go on: I may not write the file", reason, StringComparison.Ordinal);
    }

    // ── the scheduler ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Blocking_holds_what_waits_on_it_skips_what_also_waits_on_a_failure_and_comes_back_pending()
    {
        PlanStep S(string t, params Guid[] deps) => new(Guid.NewGuid(), t, StepStatus.Pending, deps);
        var a = S("a"); var b = S("b"); var c = S("c", a.Id); var d = S("d", c.Id); var e = S("e", a.Id, b.Id);
        var plan = new Plan(Guid.NewGuid(), [a, b, c, d, e]);
        var dag = new DagScheduler(plan);
        dag.NextReadyBatch(2);

        dag.MarkFailed(b.Id);
        var held = dag.MarkBlocked(a.Id);

        Assert.Equal(["c", "d"], held.Select(s => s.Title));
        var now = dag.Snapshot();
        Assert.Equal(StepStatus.Skipped, now[e.Id]);                                   // it waits on a failure too
        Assert.False(dag.HasPending);                                                    // not mistaken for a cycle

        var resumed = new DagScheduler(plan, now);
        Assert.Equal(StepStatus.Pending, resumed.Snapshot()[a.Id]);
        Assert.Equal(StepStatus.Pending, resumed.Snapshot()[d.Id]);
        Assert.Equal(StepStatus.Skipped, resumed.Snapshot()[e.Id]);
        Assert.Equal("a", Assert.Single(resumed.NextReadyBatch(4)).Title);
    }

    // ── how it reads ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_blocked_run_has_its_own_exit_code_card_word_and_inbox_line()
    {
        Assert.Equal(4, RunReport.ExitCodeFor(RunOutcomeKind.Blocked));
        Assert.Equal("Blocked — needs a permission", RunOutcomeWords.StepActivity(StepOutcomeKind.Blocked, "needs a permission"));
        Assert.Equal("error", InboxLines.For("Blocked", 0, 0, "needs a permission").Kind);
        Assert.Equal("1 step(s) blocked", RunOutcomeWords.Explain([StepOutcomeKind.Succeeded, StepOutcomeKind.Blocked], [], cycle: false));
    }
}
