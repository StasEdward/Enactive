namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Picking a run up after the process that was running it is gone.
///
/// <para><c>PLAN_v2.md</c> §11 carried this as the first of the things "claimed above, but not
/// shipped": <i>"Resume does not exist. An approval is an inline await on a TaskCompletionSource, so
/// a run waiting for a decision is waiting IN MEMORY. Close the app and the run is gone - it is not
/// resumed, because there is nothing to resume from."</i></para>
///
/// <para><b>The scope, stated before the tests rather than discovered from them.</b> A run's work
/// lives in an async iterator and in locals captured by closures - a conversation mid-turn, a tool
/// call half-dispatched, a provider stream half-read. None of that can be written to disk and picked
/// up again; resuming inside a step would mean rewriting the orchestrator as a state machine. So the
/// checkpoint is taken where the run is genuinely between things, and a step that was RUNNING when
/// the process died is <b>done again from its beginning</b>. These tests pin that cost as a
/// behaviour rather than leaving it as a hope.</para>
///
/// <para>The interruption is modelled by <see cref="RecordingCheckpointStore"/> keeping every
/// checkpoint the run wrote, and a later test resuming from one of the earlier ones. That is not a
/// convenience: killing a process in a test would make what is on disk depend on a race between the
/// checkpoint write and the kill, and a test whose subject is "what survives" cannot be one whose
/// answer varies.</para>
/// </summary>
public sealed class ResumeTests
{
    [Fact]
    public async Task Requirement_assignments_survive_checkpoint_and_are_used_by_resumed_review()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"Implement and verify","steps":[{"title":"Implement","dependsOn":[],"obligations":["O001"]},{"title":"Verify","dependsOn":[0],"obligations":["O001"]}]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"correct"}"""), Turn.Says("done"),
            Turn.Calls1("read_file", """{"path":"result.txt"}"""), Turn.Says("verified"));
        await fx.RunAsync(fx.Build(worker, checkpoints: store), "Implement and verify result.txt");
        var checkpoint = System.Text.Json.JsonSerializer.Deserialize<RunCheckpoint>(
            System.Text.Json.JsonSerializer.Serialize(store.After(1)))!;
        Assert.All(checkpoint.Steps, step => Assert.Equal(new[] { "O001" }, step.ObligationIds));

        var resumed = new FakeChatProvider(Turn.Calls1("read_file", """{"path":"result.txt"}"""), Turn.Says("verified"));
        var reviewer = new FakeChatProvider(Verdicts.Combined(Verdicts.Shown("Verified prior implementation", 1), "S1"));
        var events = await fx.ResumeAsync(fx.Build(resumed, checkpoints: store, router: Routers.WithReviewer(),
            reviewProvider: reviewer, checkSoundness: true, reviewContent: false, reviewRetries: 0), checkpoint);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Single(reviewer.Requests);
        const string map = "\"O001\":[\"S1\",\"S2\"]";
        Assert.Contains(map, string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)));
        Assert.Contains(map, string.Join("\n", resumed.Requests[0].Messages.Select(m => m.Content)));
    }

    private const string ThreeStepPlan = """
        {"disposition":"task","title":"three steps",
         "steps":[{"title":"first","dependsOn":[]},{"title":"second","dependsOn":[0]},
                  {"title":"third","dependsOn":[1]}]}
        """;

    /// <summary>
    /// A checkpoint store that keeps every version it was handed, so a test can resume from the
    /// state the run was in at any boundary - including one it was in before it finished.
    /// </summary>
    private sealed class RecordingCheckpointStore : IRunCheckpointStore
    {
        private readonly object _gate = new();

        public List<RunCheckpoint> Saved { get; } = new();
        public List<Guid> Deleted { get; } = new();

        /// <summary>The run as it stood once <paramref name="finished"/> steps were behind it.</summary>
        public RunCheckpoint After(int finished)
        {
            lock (_gate)
                return Saved.First(c => c.Finished == finished);
        }

        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct)
        {
            // The refusal to record a staged run lives in the real store, and this one mirrors it
            // so a test of that rule is testing the rule and not this fake.
            if (!checkpoint.Staged)
                lock (_gate)
                    Saved.Add(checkpoint);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        }

        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        }

        public Task DeleteAsync(Guid runId, CancellationToken ct)
        {
            lock (_gate)
                Deleted.Add(runId);
            return Task.CompletedTask;
        }
    }

    private static string[] StepsThatRan(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.StepStarted)
                 .Select(e => e.Summary)
                 .ToArray();

    // ── what gets written ───────────────────────────────────────────────────

    /// <summary>
    /// A checkpoint at every boundary: before anything runs, and after each step. The first one is
    /// the one that is easy to forget and expensive to be without - a process killed during step one
    /// would otherwise leave nothing, and the plan, which cost a model call, would have to be asked
    /// for again and would come back with different steps under different ids.
    /// </summary>
    [Fact]
    public async Task A_run_writes_a_checkpoint_at_every_step_boundary()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var provider = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };

        await fx.RunAsync(fx.Build(provider, checkpoints: store), "do three things");

        // One before the first step, then one after each of the three.
        Assert.Equal(4, store.Saved.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, store.Saved.Select(c => c.Finished).ToArray());
    }

    /// <summary>
    /// A run that reached an end leaves nothing to resume. It ran to a conclusion; offering to redo
    /// it would be offering a retry under another name, and the history already has one of those.
    /// </summary>
    [Fact]
    public async Task A_run_that_finished_forgets_its_checkpoint()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var provider = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };

        var events = await fx.RunAsync(fx.Build(provider, checkpoints: store), "do three things");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Single(store.Deleted);
        Assert.Equal(store.Saved[0].RunId, store.Deleted[0]);
    }

    /// <summary>
    /// A run that FAILED forgets its checkpoint too. Failing is an ending: something went wrong and
    /// the run said so. What makes a run resumable is that nobody knows how it ended.
    /// </summary>
    [Fact]
    public async Task A_run_that_failed_forgets_its_checkpoint_as_well()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();

        // A step that never says anything and never calls anything stalls, and the run does not
        // complete - which is an ending, not an interruption.
        var stuck = Turn.Calls1("read_file", """{"path":"nowhere.txt"}""", "r");
        var provider = new FakeChatProvider(Turn.Says(ThreeStepPlan), stuck, stuck, stuck)
        {
            WhenExhausted = stuck
        };

        var events = await fx.RunAsync(fx.Build(provider, checkpoints: store), "do three things");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Single(store.Deleted);
    }

    /// <summary>
    /// A STAGED run is never checkpointed. Its changes live in a list in memory, so a resumed staged
    /// run would build later steps on top of earlier ones that were never written - not a degraded
    /// resume but a wrong one. The engine offers nothing rather than offering that.
    /// </summary>
    [Fact]
    public async Task A_staged_run_is_not_checkpointed()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var provider = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };

        var staged = new RunSettings(2, "Assisted", "developer", Staged: true);
        await fx.RunAsync(fx.Build(provider, checkpoints: store, settings: staged), "do three things");

        Assert.Empty(store.Saved);
    }

    /// <summary>
    /// A checkpoint says what the run had concluded, not only where it had got to. Without the
    /// digest a resumed run's remaining steps would be told nothing about the ones before them -
    /// above one step at a time the digest is the ONLY thing that crosses between steps.
    /// </summary>
    [Fact]
    public async Task A_checkpoint_carries_the_plan_the_digest_and_the_transcript()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var provider = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };

        await fx.RunAsync(fx.Build(provider, checkpoints: store), "do three things");

        var afterOne = store.After(finished: 1);
        Assert.Equal(new[] { "first", "second", "third" }, afterOne.Steps.Select(s => s.Title).ToArray());
        Assert.Equal(nameof(StepStatus.Done), afterOne.Steps[0].Status);
        Assert.Equal(nameof(StepOutcomeKind.Succeeded), afterOne.Steps[0].Outcome);
        Assert.Contains(afterOne.Digest, d => d.StartsWith("first:", StringComparison.Ordinal));
        Assert.NotEmpty(afterOne.Transcript);
        Assert.Equal("do three things", afterOne.Request);
        Assert.Equal(2, afterOne.Remaining);
        Assert.True(afterOne.IsResumable);
    }

    // ── what resuming does ──────────────────────────────────────────────────

    /// <summary>
    /// The point of the whole thing: work that was done is not done again.
    ///
    /// <para>The resumed run is given a provider with NO plan turn in its script, which is the
    /// second half of the claim - a resumed run does not plan again either. Re-planning would return
    /// different steps under different ids, and every finished step in the checkpoint would then
    /// refer to nothing.</para>
    /// </summary>
    [Fact]
    public async Task Resuming_does_not_redo_the_steps_that_finished()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        var afterOne = store.After(finished: 1);

        var resumed = new RecordingCheckpointStore();
        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again, checkpoints: resumed), afterOne);
        Assert.All(again.Requests, request =>
        {
            Assert.Single(request.Messages, m => m.Content?.StartsWith("## Plan scopes") == true);
            Assert.Single(request.Messages, m => m.Content?.Contains("Original request (verbatim):") == true);
        });


        var ran = StepsThatRan(events);
        Assert.Equal(2, ran.Length);
        Assert.All(ran, s => Assert.DoesNotContain("first", s, StringComparison.Ordinal));
        Assert.Contains(ran, s => s.Contains("second", StringComparison.Ordinal));
        Assert.Contains(ran, s => s.Contains("third", StringComparison.Ordinal));

        // What the first attempt concluded is carried, not dropped. Above one step at a time the
        // digest is the ONLY thing that crosses between steps, so a resumed run that started with an
        // empty one would leave every remaining step believing it was the first.
        Assert.Contains(resumed.Saved[0].Digest, d => d.StartsWith("first:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The plan still reads whole. A step that was done before this run began gets its card anyway -
    /// a plan whose first step simply never appears reads as a plan that lost it, and the step
    /// numbers everything else is keyed by would be off by one.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_shows_the_steps_it_inherited()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again), store.After(finished: 1));

        var completions = events.Where(e => e.Kind == EventKind.StepCompleted).ToArray();
        Assert.Equal(3, completions.Length);
        Assert.All(completions, e => Assert.Equal(StepOutcomeKind.Succeeded, e.StepOutcome()));

        Assert.Contains(events, e => e.Summary.Contains("Resumed: 1 of 3", StringComparison.Ordinal));
    }

    /// <summary>
    /// A restored plan whose dependency failed is not a plan with a cycle in it.
    ///
    /// <para>Its own test because the wrong answer was so plausible: a live run cascade-skips a
    /// failed step's dependents inside <c>MarkFailed</c>, a restored run never calls it, and the
    /// dependents then sit Pending forever - which the cycle detector, correctly by its own lights,
    /// reports as unresolvable dependencies. The run stopped for the right reason under a false
    /// name, which is the kind of thing that costs somebody an afternoon on a plan that was fine.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failed_dependency_is_not_reported_as_a_cycle()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        var afterOne = store.After(finished: 1);
        var broken = afterOne with
        {
            Steps = afterOne.Steps
                .Select(s => s.Title == "second"
                    ? s with { Status = nameof(StepStatus.Failed), Outcome = nameof(StepOutcomeKind.Failed) }
                    : s)
                .ToArray()
        };

        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again), broken);

        Assert.DoesNotContain(events, e => e.Summary.Contains("cycle", StringComparison.OrdinalIgnoreCase));

        // "third" depended on "second". It is skipped, not left in limbo and not quietly run.
        Assert.Empty(StepsThatRan(events));
        var third = Assert.Single(
            events,
            e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("third", StringComparison.Ordinal));
        Assert.Equal(StepOutcomeKind.Skipped, third.StepOutcome());
    }

    /// <summary>
    /// The run carries on under the SAME TASK. That is what makes it an attempt at the work rather
    /// than unrelated work that happens to say the same thing, and it is what the history's grouping
    /// by task is built on.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_is_a_new_run_under_the_same_task()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        var checkpoint = store.After(finished: 1);
        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again), checkpoint);

        Assert.All(events, e => Assert.Equal(checkpoint.TaskId, e.TaskId));
        Assert.All(events, e => Assert.NotEqual(checkpoint.RunId, e.RunId));
        Assert.Single(events.Select(e => e.RunId).Distinct());
    }

    /// <summary>
    /// A step that was RUNNING when the process died IS done again. This is the cost of resuming at
    /// a step boundary, and it is pinned here rather than left as a footnote: the alternatives are
    /// leaving it Running, which would stall the resumed run against a step nobody is doing, and
    /// calling it Done, which would build everything after it on work that never finished.
    /// </summary>
    [Fact]
    public async Task A_step_that_was_in_progress_is_done_again()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        // The state a process killed mid-step leaves behind: one done, one in flight.
        var afterOne = store.After(finished: 1);
        var interrupted = afterOne with
        {
            Steps = afterOne.Steps
                .Select(s => s.Title == "second"
                    ? s with { Status = nameof(StepStatus.Running) }
                    : s)
                .ToArray()
        };

        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again), interrupted);

        var ran = StepsThatRan(events);
        Assert.Equal(2, ran.Length);
        Assert.Contains(ran, s => s.Contains("second", StringComparison.Ordinal));

        // And it says so, because the files that step already wrote are still in the workspace and
        // somebody reading the run should not have to work that out.
        Assert.Contains(events, e => e.Summary.Contains("done again from its beginning", StringComparison.Ordinal));
    }

    /// <summary>
    /// A resumed run's outcome accounts for the steps it inherited. A resume of a plan whose earlier
    /// step FAILED must not be able to report Completed on the strength of the steps it ran itself -
    /// the run's outcome has always been the aggregate of its steps', and inheriting steps without
    /// inheriting their outcomes would quietly break that.
    ///
    /// <para>It also pins the DIAGNOSIS, which is where the first draft of this went wrong. The
    /// failure cascade lives inside <c>MarkFailed</c>, which a restored run never calls, so the
    /// dependents of the failed step sat Pending, could never become ready, and the run reported
    /// "unresolvable dependencies (a cycle)". The outcome was right by accident and the explanation
    /// was false: the plan was fine, something it depended on had failed. The test asserted only the
    /// outcome and passed with the seeding removed, which is how the defect was found at all.</para>
    /// </summary>
    [Fact]
    public async Task An_inherited_failure_still_counts_against_the_run()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        var afterOne = store.After(finished: 1);
        var withFailure = afterOne with
        {
            Steps = afterOne.Steps
                .Select(s => s.Title == "first"
                    ? s with { Status = nameof(StepStatus.Failed), Outcome = nameof(StepOutcomeKind.Failed) }
                    : s)
                .ToArray()
        };

        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again), withFailure);

        var terminal = events.Last();
        Assert.Equal(RunOutcomeKind.Failed, terminal.Outcome());
        Assert.Contains("fail", terminal.OutcomeReason() ?? "", StringComparison.OrdinalIgnoreCase);

        // The failed step took its dependents with it, exactly as a live run would have.
        Assert.Empty(StepsThatRan(events));
        var completions = events.Where(e => e.Kind == EventKind.StepCompleted).ToArray();
        Assert.Equal(3, completions.Length);
        Assert.Equal(2, completions.Count(e => e.StepOutcome() == StepOutcomeKind.Skipped));

        // And nobody is told the plan has a cycle in it. It does not.
        Assert.DoesNotContain(events, e => e.Summary.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The files an earlier attempt produced are still this task's result. A resumed run whose
    /// closing summary named only the half it did itself would be the same defect as a record that
    /// says it has no events.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_reports_the_files_the_earlier_attempt_produced()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();

        var writesOne = Turn.Calls1("write_file", """{"path":"one.txt","content":"first"}""", "w1");
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan), writesOne)
        {
            WhenExhausted = Turn.Says("step done")
        };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        var afterOne = store.After(finished: 1);
        Assert.Contains("one.txt", afterOne.Artifacts);

        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again), afterOne);

        Assert.Contains("one.txt", events.Last().Summary);
    }

    /// <summary>
    /// A resume forgets the checkpoint it came FROM, not only the one it wrote itself.
    ///
    /// <para>A resume mints a NEW run id on purpose - the second attempt is its own run in the
    /// history, under the same task - and the ending deleted that id. The interrupted run's file is
    /// keyed by the OLD one, and nothing anywhere removed it. So the offer to resume outlived the
    /// work it was an offer to finish: the task ran to completion, and the UNFINISHED card stayed on
    /// screen, still amber, still saying nobody knew how this ended. Pressing it again started the
    /// same plan a second time.</para>
    ///
    /// <para>Both ids are asserted, because the fix must ADD the one it came from rather than
    /// replace the one it wrote - a run that forgot only its origin would leave its own checkpoint
    /// behind and move the same defect one attempt along.</para>
    /// </summary>
    [Fact]
    public async Task A_resumed_run_that_ends_forgets_the_checkpoint_it_came_from()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var first = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };
        await fx.RunAsync(fx.Build(first, checkpoints: store), "do three things");

        var afterOne = store.After(finished: 1);

        var resumed = new RecordingCheckpointStore();
        var again = new FakeChatProvider() { WhenExhausted = Turn.Says("step done") };
        var events = await fx.ResumeAsync(fx.Build(again, checkpoints: resumed), afterOne);

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(afterOne.RunId, resumed.Deleted);
        Assert.Contains(resumed.Saved[0].RunId, resumed.Deleted);
    }

    /// <summary>
    /// A quick action leaves no checkpoint. It is one action with no boundary inside it, so there is
    /// no place a resume could pick it up from - and a checkpoint that could never be resumed is an
    /// offer the engine cannot keep.
    /// </summary>
    [Fact]
    public async Task A_quick_action_leaves_nothing_to_resume()
    {
        using var fx = new EngineFixture();
        var store = new RecordingCheckpointStore();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("done"));

        await fx.RunAsync(fx.Build(provider, checkpoints: store), "do the thing");

        Assert.Empty(store.Saved);
    }

    /// <summary>
    /// Nothing is written when no store was given. That is every test written before resume existed,
    /// and every host that has not asked for it.
    /// </summary>
    [Fact]
    public async Task Without_a_store_nothing_is_checkpointed()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(ThreeStepPlan)) { WhenExhausted = Turn.Says("step done") };

        var events = await fx.RunAsync(fx.Build(provider), "do three things");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }
}
