namespace Enactive.Engine.Tests;

using System.Collections.Concurrent;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Xunit;
using Waves = WavesAreValidatedAndAttributedTests;

/// <summary>
/// Plan Phase 4.2: a host asks a run to pause. Nothing new is started, the steps running finish - unlike Stop, which
/// cancels them - and the run ends PAUSED with a checkpoint that says so. Resumed, it runs exactly the steps that are
/// left. Deliberately not code: invoices, receipts and a summary of them.
/// </summary>
public sealed class APausedRunCarriesOnTests
{
    private const string Invoices = "Count the invoices";
    private const string Receipts = "Count the receipts";
    private const string Summary = "Write the summary";
    private const string Ready = "Say it is ready";

    private const string Plan = """
        {"disposition":"task","title":"books",
         "steps":[{"title":"Count the invoices","dependsOn":[]},
                  {"title":"Count the receipts","dependsOn":[]},
                  {"title":"Write the summary","dependsOn":[0,1]},
                  {"title":"Say it is ready","dependsOn":[2]}]}
        """;

    /// <summary>A checkpoint store that keeps one checkpoint per run, as the file store does, and can be made to refuse.</summary>
    private sealed class Store : IRunCheckpointStore
    {
        private readonly Dictionary<Guid, RunCheckpoint> _kept = new();
        public List<RunCheckpoint> Saved { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public Func<RunCheckpoint, bool>? Refuse { get; set; }

        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct)
        {
            if (Refuse?.Invoke(checkpoint) == true) throw new IOException("the disk is full");
            lock (_kept) { _kept[checkpoint.RunId] = checkpoint; Saved.Add(checkpoint); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct)
        {
            lock (_kept) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(_kept.Values.OrderByDescending(c => c.At).ToArray());
        }

        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) return Task.FromResult(_kept.GetValueOrDefault(runId));
        }

        public Task DeleteAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) { _kept.Remove(runId); Deleted.Add(runId); }
            return Task.CompletedTask;
        }

        public IReadOnlyList<RunCheckpoint> Kept
        {
            get { lock (_kept) return _kept.Values.ToArray(); }
        }
    }

    private const string StepMarker = "Proceed with this step of the plan: ";

    private static string? TitleOf(Enactive.Core.Chat.ChatRequest request)
    {
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            var content = request.Messages[i].Content;
            var at = content?.IndexOf(StepMarker, StringComparison.Ordinal) ?? -1;
            if (at < 0) continue;
            var from = at + StepMarker.Length;
            var end = content!.IndexOf('\n', from);
            return (end < 0 ? content[from..] : content[from..end]).Trim();
        }
        return null;
    }

    /// <summary>
    /// The pause is asked for while every one of <paramref name="titles"/> is RUNNING - each has sent its first request
    /// - and none of them answers before it is: so they are in flight when the pause arrives, and finish after it.
    /// </summary>
    private static void PauseWhileRunning(ByStepChatProvider provider, RunPause pause, params string[] titles)
    {
        var arrived = new ConcurrentDictionary<string, byte>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeStreamRequest = async (request, ct) =>
        {
            if (TitleOf(request) is not { } title || !titles.Contains(title)) return;
            if (arrived.TryAdd(title, 0) && arrived.Count == titles.Length)
            {
                pause.Request();
                all.TrySetResult();
            }
            await all.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        };
    }

    private static IEnumerable<WorkEvent> Completed(IEnumerable<WorkEvent> events) => events.Where(e => e.Kind == EventKind.StepCompleted);

    private static ByStepChatProvider Books() => new ByStepChatProvider(Plan)
        .Step(Invoices, Turn.Says("There are 3 invoices."))
        .Step(Receipts, Turn.Says("There are 5 receipts."))
        .Step(Summary, Turn.Says("Summary: 3 invoices, 5 receipts."))
        .Step(Ready, Turn.Says("Ready."));

    /// <summary>THE ONE THAT MATTERS: two steps in flight finish, nothing is skipped, and the run ends Paused with a checkpoint saying so.</summary>
    [Fact]
    public async Task Pausing_with_two_steps_in_flight_lets_both_finish_and_ends_paused()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store();
        var worker = Books();
        PauseWhileRunning(worker, fx.Pause, Invoices, Receipts);

        var events = await fx.RunAsync(fx.Build(worker, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Paused, events.Last().Outcome());
        Assert.StartsWith("Paused at step 2 of 4", events.Last().OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(EventKind.TaskFailed, events.Last().Kind);                       // only Completed is TaskCompleted
        Assert.StartsWith("Paused at step 2 of 4", events.Last().Summary, StringComparison.Ordinal); // not "Paused: Paused at"
        var done = Completed(events).ToArray();
        Assert.Equal(2, done.Length);                                                 // both finished - not cancelled
        Assert.All(done, e => Assert.Equal(StepOutcomeKind.Succeeded, e.StepOutcome()));
        Assert.DoesNotContain(done, e => e.StepOutcome() == StepOutcomeKind.Skipped); // no skip cascade, no AbandonPending
        Assert.Empty(worker.RequestsFor(Summary));                                    // nothing new started
        Assert.Empty(worker.RequestsFor(Ready));

        var kept = Assert.Single(store.Kept);                                         // kept, not forgotten
        Assert.True(kept.Paused);
        Assert.True(kept.IsResumable);
        Assert.Equal(2, kept.Remaining);
        Assert.Equal(["Done", "Done", "Pending", "Pending"], kept.Steps.Select(s => s.Status));
        Assert.DoesNotContain(kept.Steps, s => s.Status == "Running");                // a resume redoes nothing
    }

    /// <summary>Resumed, the paused run runs exactly the steps that are left, through the ordinary resume path, and completes.</summary>
    [Fact]
    public async Task A_paused_run_resumes_with_exactly_the_steps_that_are_left()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store();
        var first = Books();
        PauseWhileRunning(first, fx.Pause, Invoices, Receipts);
        var paused = await fx.RunAsync(fx.Build(first, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");
        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());

        // As a host finds it: the task's latest resumable checkpoint (ParkedRuns), and a new run with no pause asked for.
        var checkpoint = await ParkedRuns.CheckpointForAsync(store, paused.Last().TaskId, default);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Paused);
        fx.Pause = null;
        var second = new ByStepChatProvider(Plan) { Resumed = true }
            .Step(Summary, Turn.Says("Summary: 3 invoices, 5 receipts."))
            .Step(Ready, Turn.Says("Ready."));

        var resumed = await fx.ResumeAsync(fx.Build(second, maxParallelSteps: 2, checkpoints: store), checkpoint);

        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
        Assert.Empty(second.RequestsFor(Invoices));                                   // nothing redone
        Assert.Empty(second.RequestsFor(Receipts));
        Assert.Equal([Summary, Ready], resumed.Where(e => e.Kind == EventKind.StepStarted)
            .Select(e => e.Summary.Contains(Summary, StringComparison.Ordinal) ? Summary : e.Summary.Contains(Ready, StringComparison.Ordinal) ? Ready : e.Summary));
        Assert.DoesNotContain(resumed, e => e.Summary.Contains("is being done again from its beginning", StringComparison.Ordinal));
        Assert.Empty(store.Kept);                                                     // ended: both checkpoints forgotten
    }

    /// <summary>
    /// Only the pause marker is lost when its write fails: the step boundary saved on the way - the same state without
    /// the flag - is kept, the run says it is offered as an interrupted run, and resumed it finishes the steps left.
    /// It used to delete that boundary too, and one failed write meant running the whole task again.
    /// </summary>
    [Fact]
    public async Task A_pause_whose_marker_cannot_be_saved_keeps_the_last_boundary_and_says_so()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store { Refuse = c => c.Paused };
        var worker = Books();
        PauseWhileRunning(worker, fx.Pause, Invoices, Receipts);

        var events = await fx.RunAsync(fx.Build(worker, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.StartsWith("Incomplete: Paused at step 2 of 4", events.Last().Summary, StringComparison.Ordinal); // not paused: says so
        var reason = events.Last().OutcomeReason()!;
        Assert.Contains("the pause could not be saved: the disk is full", reason, StringComparison.Ordinal);
        Assert.Contains("offered as an interrupted run - resume it to carry on with the 2 step(s) left", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(store.Saved, c => c.Paused);
        var kept = Assert.Single(store.Kept);                                         // kept, not deleted
        Assert.False(kept.Paused);
        Assert.Equal(["Done", "Done", "Pending", "Pending"], kept.Steps.Select(s => s.Status));
        Assert.Empty(store.Deleted);

        fx.Pause = null;
        var second = new ByStepChatProvider(Plan) { Resumed = true };
        var resumed = await fx.ResumeAsync(fx.Build(second, maxParallelSteps: 2, checkpoints: new Store()), kept);
        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
        Assert.Empty(second.RequestsFor(Invoices));
    }

    /// <summary>On a resume, the paused checkpoint it began from is a place to carry on from as well: a failed marker keeps it.</summary>
    [Fact]
    public async Task A_resumed_run_whose_pause_cannot_be_saved_keeps_the_pause_it_began_from()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store();
        var first = Books();
        PauseWhileRunning(first, fx.Pause, Invoices, Receipts);
        await fx.RunAsync(fx.Build(first, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");
        var once = Assert.Single(store.Kept);

        fx.Pause = new RunPause();
        store.Refuse = c => c.Paused;
        var second = new ByStepChatProvider(Plan) { Resumed = true };
        PauseWhileRunning(second, fx.Pause, Summary);
        var again = await fx.ResumeAsync(fx.Build(second, maxParallelSteps: 2, checkpoints: store), once);

        Assert.Equal(RunOutcomeKind.Incomplete, again.Last().Outcome());
        Assert.Contains("offered as an interrupted run", again.Last().OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains(store.Kept, c => c.RunId == once.RunId && c.Paused);
        Assert.Contains(store.Kept, c => c.RunId == again.Last().RunId && !c.Paused && c.Remaining == 1);
    }

    /// <summary>Nothing saved at all - not the pause, not a boundary - and the run says nothing can be resumed.</summary>
    [Fact]
    public async Task A_pause_with_nothing_saved_says_nothing_can_be_resumed()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store { Refuse = _ => true };
        var worker = Books();
        PauseWhileRunning(worker, fx.Pause, Invoices, Receipts);

        var events = await fx.RunAsync(fx.Build(worker, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        var reason = events.Last().OutcomeReason()!;
        Assert.Contains("the pause could not be saved: the disk is full, and no step boundary of this run was saved either", reason, StringComparison.Ordinal);
        Assert.Contains("nothing can be resumed", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("offered", reason, StringComparison.Ordinal);
        Assert.Empty(store.Kept);
    }

    /// <summary>A run that keeps no checkpoint at all is not paused either: nothing could carry it on, and it says so.</summary>
    [Fact]
    public async Task A_pause_with_nowhere_to_keep_a_checkpoint_does_not_claim_to_be_resumable()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        fx.Pause.Request();

        var events = await fx.RunAsync(fx.Build(Books(), maxParallelSteps: 2), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("this run keeps no checkpoints, so nothing can be resumed", events.Last().OutcomeReason(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A staged run takes no pause, as a quick action takes none: it cannot be resumed - its proposals are in memory and
    /// its checkpoints are never kept - so held it would stop with its steps undone. It runs on, and says the pause was
    /// not taken.
    /// </summary>
    [Fact]
    public async Task A_staged_run_does_not_take_a_pause_and_runs_on_to_its_end()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        fx.Pause.Request();
        var store = new Store();
        var worker = Books();

        var events = await fx.RunAsync(fx.Build(worker, checkpoints: store,
            settings: new RunSettings(2, "Execute", "developer", Staged: true)), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(4, Completed(events).Count());
        Assert.NotEmpty(worker.RequestsFor(Ready));
        Assert.Single(events, e => e.Kind == EventKind.ContextAssembled
                                   && e.Summary.StartsWith("A pause was asked for and not taken", StringComparison.Ordinal));
        Assert.DoesNotContain(store.Saved, c => c.Paused);
    }

    /// <summary>A paused run that carries a blocked step says so: a resume tries it again, it is not simply left.</summary>
    [Fact]
    public async Task A_paused_run_with_a_blocked_step_says_it_is_tried_again_on_resume()
    {
        const string File = "File the receipts";
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store();
        var worker = new ByStepChatProvider("""
            {"disposition":"task","title":"books",
             "steps":[{"title":"Count the invoices","dependsOn":[]},
                      {"title":"Count the receipts","dependsOn":[]},
                      {"title":"File the receipts","dependsOn":[1]}]}
            """)
            .Step(Invoices, Turn.Calls1("read_file", """{"path":"invoices/march.txt"}""", "r1"), Turn.Says("There is no March invoice."))
            .Step(Receipts, Turn.Says("There are 5 receipts."));
        PauseWhileRunning(worker, fx.Pause, Invoices, Receipts);

        var events = await fx.RunAsync(fx.Build(worker, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");

        Assert.Equal(StepOutcomeKind.Blocked, Assert.Single(Completed(events), e => e.Summary.Contains(Invoices)).StepOutcome());
        Assert.Equal(RunOutcomeKind.Paused, events.Last().Outcome());
        Assert.Equal("Paused at step 1 of 3: 1 step(s) settled, 2 left, 1 of them blocked - tried again when the run is "
                     + "resumed - resume the run to carry on with them.", events.Last().OutcomeReason());
        Assert.Empty(worker.RequestsFor(File));
        Assert.Contains(Assert.Single(store.Kept).Steps, s => s.Status == "Blocked");
    }

    /// <summary>A pause with nothing left that could start - all that is left waits on a blocked step - is no pause: the run is Blocked.</summary>
    [Fact]
    public async Task A_pause_with_only_blocked_work_left_ends_blocked_not_paused()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store();
        var worker = Books()
            .Step(Invoices, Turn.Calls1("read_file", """{"path":"invoices/march.txt"}""", "r1"), Turn.Says("There is no March invoice."));
        PauseWhileRunning(worker, fx.Pause, Invoices, Receipts);

        var events = await fx.RunAsync(fx.Build(worker, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Blocked, events.Last().Outcome());
        Assert.DoesNotContain(store.Kept, c => c.Paused);
    }

    /// <summary>Asked for while nothing is running - here, before the first step - the run ends Paused at once.</summary>
    [Fact]
    public async Task A_pause_with_nothing_in_flight_ends_the_run_at_once()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        fx.Pause.Request();
        var store = new Store();
        var worker = Books();

        var events = await fx.RunAsync(fx.Build(worker, checkpoints: store), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Paused, events.Last().Outcome());
        Assert.Equal("Paused before the first of 4 step(s): 4 left - resume the run to carry on with them.", events.Last().OutcomeReason());
        Assert.Equal(events.Last().OutcomeReason(), events.Last().Summary);           // said once, not "Paused: Paused before"
        Assert.DoesNotContain(events, e => e.Kind is EventKind.StepStarted or EventKind.StepCompleted);
        Assert.Empty(worker.RequestsFor(Invoices));
        var kept = Assert.Single(store.Kept);
        Assert.True(kept.Paused);
        Assert.Equal(4, kept.Remaining);
    }

    /// <summary>Between steps, one at a time: the step running finishes, and the one after it does not start.</summary>
    [Fact]
    public async Task One_step_at_a_time_the_step_running_finishes_and_the_next_does_not_start()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store();
        var worker = Books();
        PauseWhileRunning(worker, fx.Pause, Invoices);

        var events = await fx.RunAsync(fx.Build(worker, checkpoints: store), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Paused, events.Last().Outcome());
        Assert.StartsWith("Paused at step 1 of 4", events.Last().OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, Assert.Single(Completed(events)).StepOutcome());
        Assert.Empty(worker.RequestsFor(Receipts));
    }

    /// <summary>A pause during a wave does not skip its validation: the wave is validated, then the run ends Paused.</summary>
    [Fact]
    public async Task A_pause_during_a_wave_still_validates_the_wave_before_the_run_ends()
    {
        using var fx = Waves.Wiki();
        fx.Pause = new RunPause();
        var store = new Store();
        const string Gamma = "Gamma builds on both";
        var worker = new ByStepChatProvider(Waves.Plan((Waves.Alpha, []), (Waves.Beta, []), (Gamma, [0, 1])));
        worker.Step(Waves.Alpha, Waves.Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        worker.Step(Waves.Beta, Waves.Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));
        worker.Step(Gamma, Waves.Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));
        PauseWhileRunning(worker, fx.Pause, Waves.Alpha, Waves.Beta);

        var events = (await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), maxParallelSteps: 2, checkpoints: store),
            "write the pages")).ToList();

        Assert.Equal(RunOutcomeKind.Paused, events.Last().Outcome());
        var validated = events.FindIndex(e => e.Summary.StartsWith("Wave 1 (steps 1 and 2)", StringComparison.Ordinal));
        Assert.True(validated >= 0, string.Join("\n", events.Select(e => e.Summary)));
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("wave 1", StringComparison.Ordinal));
        Assert.Empty(worker.RequestsFor(Gamma));
        var kept = Assert.Single(store.Kept);
        Assert.True(kept.Paused);
        Assert.Equal(1, kept.Waves!.Closed);                                          // validated before the checkpoint
        Assert.Empty(kept.Waves.Open);
    }

    /// <summary>
    /// A paused run has not ended: as for a blocked one, the answers it was given and the baseline from before its first
    /// attempt are kept for the run that carries it on - and forgotten once that one does end.
    /// </summary>
    [Fact]
    public async Task A_paused_run_keeps_its_answers_and_its_baseline_until_it_ends()
    {
        using var fx = Waves.Wiki();
        fx.Pause = new RunPause();
        var store = new Store();
        var taskId = Guid.NewGuid();
        var ledger = new DecisionLedger(fx.Root);
        var asked = ledger.Park(new DecisionRequest(taskId, "Write the pages?", "write_file pages/a.page",
            [new DecisionOption("allow", "Allow"), new DecisionOption("deny", "Deny")], "allow"));
        Assert.True(ledger.Answer(taskId, asked.RequestId, "allow"));
        var worker = Books();
        PauseWhileRunning(worker, fx.Pause, Invoices, Receipts);

        var paused = await Run(fx, fx.Build(worker, maxParallelSteps: 2, checkpoints: store), taskId);

        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());
        Assert.Single(ledger.For(taskId));
        Assert.NotNull(new BaselineStore(fx.Root).Load(taskId));

        fx.Pause = null;
        var resumed = await fx.ResumeAsync(fx.Build(new ByStepChatProvider(Plan) { Resumed = true }, maxParallelSteps: 2,
            checkpoints: store), Assert.Single(store.Kept));

        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
        Assert.Empty(ledger.For(taskId));
        Assert.Null(new BaselineStore(fx.Root).Load(taskId));
    }

    /// <summary>The regression guard: a run given no pause, or one never asked for, runs as it always did.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_pause_asked_for_the_run_is_as_it_was(bool given)
    {
        using var fx = new EngineFixture { Pause = given ? new RunPause() : null };
        var store = new Store();
        var worker = Books();

        var events = await fx.RunAsync(fx.Build(worker, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(4, Completed(events).Count());
        Assert.DoesNotContain(store.Saved, c => c.Paused);
        Assert.Empty(store.Kept);
    }

    /// <summary>A resumed run that is paused again leaves one checkpoint to carry on from - not that one and the one it began from.</summary>
    [Fact]
    public async Task A_resumed_run_paused_again_leaves_one_checkpoint()
    {
        using var fx = new EngineFixture { Pause = new RunPause() };
        var store = new Store();
        var first = Books();
        PauseWhileRunning(first, fx.Pause, Invoices, Receipts);
        await fx.RunAsync(fx.Build(first, maxParallelSteps: 2, checkpoints: store), "count the books and summarise them");
        var once = Assert.Single(store.Kept);

        fx.Pause = new RunPause();
        var second = new ByStepChatProvider(Plan) { Resumed = true }.Step(Summary, Turn.Says("Summary written."));
        PauseWhileRunning(second, fx.Pause, Summary);
        var again = await fx.ResumeAsync(fx.Build(second, maxParallelSteps: 2, checkpoints: store), once);

        Assert.Equal(RunOutcomeKind.Paused, again.Last().Outcome());
        Assert.StartsWith("Paused at step 3 of 4", again.Last().OutcomeReason(), StringComparison.Ordinal);
        var twice = Assert.Single(store.Kept);
        Assert.NotEqual(once.RunId, twice.RunId);
        Assert.True(twice.Paused);
        Assert.Equal(1, twice.Remaining);
    }

    /// <summary>
    /// What tells a pause that held work back from a plan that can go no further: HasReady only looks. It marks
    /// nothing, and it is false for pending steps that can never become ready - which the run reports as a cycle.
    /// </summary>
    [Fact]
    public void A_pause_is_told_from_a_cycle_by_what_could_start_now()
    {
        Enactive.Core.Tasks.PlanStep S(Guid id, string t, params Guid[] deps) => new(id, t, Enactive.Core.Tasks.StepStatus.Pending, deps);
        var (a, b, x, y) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dag = new DagScheduler(new Enactive.Core.Tasks.Plan(Guid.NewGuid(), [S(a, "a"), S(b, "b", a), S(x, "x", y), S(y, "y", x)]));

        Assert.True(dag.HasReady);
        Assert.True(dag.HasReady);                                                    // asking twice changes nothing
        Assert.Equal("a", Assert.Single(dag.NextReadyBatch(4)).Title);                // and nothing was claimed by asking
        Assert.False(dag.HasReady);                                                   // a is running; b waits for it
        dag.MarkDone(a);
        Assert.True(dag.HasReady);
        Assert.Equal("b", Assert.Single(dag.NextReadyBatch(4)).Title);
        dag.MarkDone(b);
        Assert.True(dag.HasPending);                                                  // x and y wait on each other
        Assert.False(dag.HasReady);
    }

    private static async Task<List<WorkEvent>> Run(EngineFixture fx, Orchestrator engine, Guid taskId)
    {
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var intent = new Intent(taskId, "count the books and summarise them", IntentSource.CommandBar, context, DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<WorkEvent>();
        await foreach (var ev in engine.SubmitIntentAsync(intent, cts.Token))
            events.Add(ev);
        return events;
    }
}
