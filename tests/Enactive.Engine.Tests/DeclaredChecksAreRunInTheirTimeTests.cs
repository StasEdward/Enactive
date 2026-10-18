namespace Enactive.Engine.Tests;

using System.Collections.Concurrent;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 3.2, fix round 1: a declared check is given its own time, not a worker's; an obligation is discharged only
/// once it has run; a check owed at a wave's end is never lost to a pause or a crash; and the quick action, the unattended
/// step and the engine's own required checks are each decided as the rest.
/// </summary>
public sealed class DeclaredChecksAreRunInTheirTimeTests
{
    private const string Failing = "echo LINT-ERROR-42 && exit 1";
    private const string Passing = "exit 0";
    private const string OneStep = """{"disposition":"task","title":"app","steps":[{"title":"Write the app","dependsOn":[]}]}""";
    private const string Quick = """{"disposition":"quick_action","title":"write the app"}""";

    /// <summary>A command that takes about <paramref name="seconds"/> seconds, on cmd and on sh.</summary>
    private static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 >nul" : $"sleep {seconds}";

    private static ResolvedCheck Check(string id, string command, CheckFire fire, CheckSeverity severity, int timeout = CheckLimits.DefaultTimeoutSeconds,
        params string[] matches)
        => new(new CheckDefinition(id, null, command, matches, fire, severity, timeout, null), CheckOrigin.Workspace);

    private static CheckSnapshot Snapshot(params ResolvedCheck[] checks) => new(checks, []);

    private static Turn Writes(string path, string content, string id)
        => Turn.Calls1("write_file", JsonSerializer.Serialize(new { path, content }), id);

    private static WorkEvent[] Checks(IEnumerable<WorkEvent> events, string label)
        => events.Where(e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains(label, StringComparison.Ordinal)).ToArray();

    private static WorkEvent StepEnd(IEnumerable<WorkEvent> events, string title)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains(title, StringComparison.Ordinal));

    /// <summary>The shipped tools, with a worker's command limit of one second - so a test sees the limit without a minute's wait.</summary>
    private static ITool[] WithWorkerLimit(int seconds)
        => EngineFixture.ShippedTools().Select(t => t.Definition.Name == "run_command" ? new RunCommandTool(seconds) : t).ToArray();

    // ── a check's own time ─────────────────────────────────────────────────────────────────

    /// <summary>The command tool: a worker's call stops at the worker's limit; the engine's own run takes the time it gives.</summary>
    [Fact]
    public async Task The_command_tool_gives_the_engine_its_own_time_and_a_worker_only_the_workers()
    {
        using var fx = new EngineFixture();
        var tool = new RunCommandTool(1);
        var args = JsonSerializer.Serialize(new { command = Sleep(2) });

        var worker = await tool.InvokeAsync(args, fx.ContextFor(), CancellationToken.None);
        Assert.False(worker.Success);
        Assert.Contains("timed out after 1s", worker.Error, StringComparison.Ordinal);
        Assert.Equal(true, worker.Metadata[CommandResultKeys.TimedOut]);
        Assert.Equal(1, worker.Metadata[CommandResultKeys.TimeoutSeconds]);

        var engine = await tool.InvokeAsync(args, fx.ContextFor() with { CommandTimeoutSeconds = 10 }, CancellationToken.None);
        Assert.True(engine.Success, engine.Error);

        // Not something a model can ask for: the schema it is shown has no timeout.
        Assert.DoesNotContain("timeout", tool.Definition.JsonSchema, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A check longer than a worker's command may take, and shorter than its own timeout, passes - and the worker's own
    /// command in the same run still stops at the worker's limit.
    /// </summary>
    [Fact]
    public async Task A_check_longer_than_a_workers_limit_and_within_its_own_passes()
    {
        using var fx = new EngineFixture
        {
            ToolsOverride = WithWorkerLimit(1),
            Checks = Snapshot(Check("slow", Sleep(2), CheckFire.Run, CheckSeverity.Blocking, timeout: 10))
        };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Turn.Calls1("run_command", JsonSerializer.Serialize(new { command = Sleep(2) }), "c1"),
            Turn.Says("It took too long."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "wait a little");

        Assert.Contains(worker.Requests.SelectMany(r => r.Messages), m => m.Content?.Contains("timed out after 1s", StringComparison.Ordinal) == true);
        Assert.StartsWith("PASS", Assert.Single(Checks(events, "slow (the run)")).Summary, StringComparison.Ordinal);
        // The step itself is short of done - its own command never finished - but nothing about the check holds the run.
        Assert.DoesNotContain("slow", events.Last().OutcomeReason() ?? "", StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Failed, events.Last().Outcome());
    }

    /// <summary>A check that outlasts its own timeout did not finish - said so, not "no exit code" - and, blocking, holds the run back.</summary>
    [Fact]
    public async Task A_check_that_outlasts_its_timeout_is_said_not_to_have_finished()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("slow", Sleep(4), CheckFire.Run, CheckSeverity.Blocking, timeout: 1)) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Turn.Says("Nothing to do."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "wait a little");

        var check = Assert.Single(Checks(events, "slow (the run)"));
        Assert.StartsWith("NOT CHECKED", check.Summary, StringComparison.Ordinal);
        Assert.Contains("not run to its end: it did not finish within its 1 second(s)", check.Summary, StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── only what ran is discharged ──────────────────────────────────────────────────────────

    /// <summary>Stopped while the wave's checks run: the one that ran is discharged, the one it did not reach is still owed.</summary>
    [Fact]
    public async Task A_stop_during_the_wave_checks_leaves_the_ones_not_run_owed()
    {
        using var fx = new EngineFixture();
        var orchestrator = fx.Build(new FakeChatProvider(), EngineFixture.Role("developer"));
        var session = new RunSession(new RunScope(Guid.NewGuid(), Guid.NewGuid(), new RunBudget(ExecutionLimits.None, DateTimeOffset.UtcNow), []), [])
        {
            Checks = Snapshot(Check("quick", Passing, CheckFire.Step, CheckSeverity.Blocking, matches: "src/**"),
                Check("slow", Sleep(10), CheckFire.Step, CheckSeverity.Blocking, matches: "src/**"))
        };
        session.PendingChecks.Add(new PendingCheck("quick", CheckBoundary.Wave, "owed") { Wave = 1 });
        session.PendingChecks.Add(new PendingCheck("slow", CheckBoundary.Wave, "owed") { Wave = 1 });
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.WaveChecksAsync(session, 1, "steps 1 and 2", context, _ => ValueTask.CompletedTask, stop.Token));

        Assert.Equal(["slow"], session.PendingChecks.Select(p => p.CheckId));
    }

    // ── a wave's checks and a pause ─────────────────────────────────────────────────────────

    private const string Alpha = "Alpha writes page a";
    private const string Beta = "Beta writes page b";
    private const string Gamma = "Gamma builds on both";

    private sealed class Store : IRunCheckpointStore
    {
        private readonly Dictionary<Guid, RunCheckpoint> _kept = new();
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (_kept) _kept[checkpoint.RunId] = checkpoint; return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) { lock (_kept) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(_kept.Values.ToArray()); }
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) { lock (_kept) return Task.FromResult(_kept.GetValueOrDefault(runId)); }
        public Task DeleteAsync(Guid runId, CancellationToken ct) { lock (_kept) _kept.Remove(runId); return Task.CompletedTask; }
        public IReadOnlyList<RunCheckpoint> Kept { get { lock (_kept) return _kept.Values.ToArray(); } }
    }

    private static void PauseWhileRunning(ByStepChatProvider provider, RunPause pause, params string[] titles)
    {
        var arrived = new ConcurrentDictionary<string, byte>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeStreamRequest = async (request, ct) =>
        {
            var title = titles.FirstOrDefault(t => request.Messages.Any(m => m.Content?.Contains("Proceed with this step of the plan: " + t, StringComparison.Ordinal) == true));
            if (title is null) return;
            if (arrived.TryAdd(title, 0) && arrived.Count == titles.Length)
            {
                pause.Request();
                all.TrySetResult();
            }
            await all.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        };
    }

    /// <summary>
    /// With waves kept, a pause never strands a check owed at the wave's end: the dispatcher holds for the pause, the wave
    /// drains, and its boundary - checks included - runs before the run stops. So the paused checkpoint owes nothing. A
    /// checkpoint that does (a step boundary written before a process died, ahead of its wave's end) has the check run at
    /// the resumed run's first boundary, before anything new starts.
    /// </summary>
    [Fact]
    public async Task A_check_owed_at_a_wave_is_run_before_a_pause_and_at_a_resumed_runs_first_boundary()
    {
        var plan = WavesAreValidatedAndAttributedTests.Plan((Alpha, []), (Beta, []), (Gamma, [0, 1]));
        using var fx = WavesAreValidatedAndAttributedTests.Wiki();
        fx.Pause = new RunPause();
        fx.Checks = Snapshot(Check("lint", Passing, CheckFire.Step, CheckSeverity.Blocking, matches: "pages/**"));
        var store = new Store();
        var first = new ByStepChatProvider(plan);
        first.Step(Alpha, WavesAreValidatedAndAttributedTests.Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        first.Step(Beta, WavesAreValidatedAndAttributedTests.Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));
        PauseWhileRunning(first, fx.Pause, Alpha, Beta);

        var paused = await fx.RunAsync(fx.Build(first, EngineFixture.Role("developer"), maxParallelSteps: 2, checkpoints: store), "write the pages");

        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());
        Assert.StartsWith("PASS", Assert.Single(Checks(paused, "lint (wave 1: steps 1 and 2)")).Summary, StringComparison.Ordinal);
        var checkpoint = Assert.Single(store.Kept);
        Assert.Null(checkpoint.PendingChecks);

        // As a process killed between a step's boundary and its wave's end would have left it.
        var owing = checkpoint with { PendingChecks = [new PendingCheck("lint", CheckBoundary.Wave, "owed at the end of wave 1") { Wave = 1 }] };
        fx.Pause = null;
        var second = new ByStepChatProvider(plan) { Resumed = true }
            .Step(Gamma, WavesAreValidatedAndAttributedTests.Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));
        var resumed = (await fx.ResumeAsync(fx.Build(second, EngineFixture.Role("developer"), maxParallelSteps: 2, checkpoints: new Store()), owing)).ToList();

        // The carried obligation, at the resumed run's first boundary (its current wave, 2) - then Gamma's own, at its wave.
        var ran = Assert.Single(Checks(resumed, "lint (wave 2):"));
        Assert.Single(Checks(resumed, "lint (wave 2: step 3)"));
        var gammaStarted = resumed.FindIndex(e => e.Kind == EventKind.StepStarted && e.Summary.Contains(Gamma, StringComparison.Ordinal));
        Assert.True(gammaStarted > resumed.IndexOf(ran), resumed.Text());
        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
    }

    /// <summary>
    /// A blocking check that did not run at a wave's end (it outlasted its time) holds the run back - and still does after
    /// a pause: the paused checkpoint keeps what it came to, and the resumed run, whose one step writes nothing and so
    /// owes no check of its own, is not Completed on it. Without the pause the run is Incomplete; a pause is no way out.
    /// </summary>
    [Fact]
    public async Task A_blocking_wave_check_that_did_not_run_still_holds_the_run_back_after_a_pause()
    {
        var (fx, store, checkpoint, plan) = await PausedAfterAWaveCheckThatDidNotRunAsync();
        using var _ = fx;

        var held = Assert.Single(checkpoint.HeldBackBy ?? []);
        Assert.Equal(CriterionOutcome.Unknown, held.Outcome);
        Assert.Equal("slow", held.Check?.CheckId);
        Assert.Null(held.Output);

        var resumed = await ResumeWithAStepThatWritesNothingAsync(fx, plan, checkpoint);

        Assert.Empty(Checks(resumed, "slow ("));
        Assert.Equal(RunOutcomeKind.Incomplete, resumed.Last().Outcome());
        Assert.Contains("slow", resumed.Last().OutcomeReason() ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The same from a process killed after the wave's boundary: what it leaves is a checkpoint written there, not a paused
    /// one - and the resumed run is held back on the check all the same.
    /// </summary>
    [Fact]
    public async Task A_blocking_wave_check_that_did_not_run_still_holds_back_a_run_resumed_after_a_crash()
    {
        var (fx, store, checkpoint, plan) = await PausedAfterAWaveCheckThatDidNotRunAsync();
        using var _ = fx;

        // Read back from disk, as a new process would: what holds the run back must survive the file, not only memory.
        var disk = new JsonCheckpointStore(fx.Workspace);
        await disk.SaveAsync(checkpoint with { Paused = false }, CancellationToken.None);
        var left = await disk.LoadAsync(checkpoint.RunId, CancellationToken.None);
        Assert.Equal(CriterionOutcome.Unknown, Assert.Single(left!.HeldBackBy ?? []).Outcome);

        var resumed = await ResumeWithAStepThatWritesNothingAsync(fx, plan, left);

        Assert.Equal(RunOutcomeKind.Incomplete, resumed.Last().Outcome());
        Assert.Contains("slow", resumed.Last().OutcomeReason() ?? "", StringComparison.Ordinal);
    }

    private static async Task<(EngineFixture Fx, Store Store, RunCheckpoint Checkpoint, string Plan)> PausedAfterAWaveCheckThatDidNotRunAsync()
    {
        var plan = WavesAreValidatedAndAttributedTests.Plan((Alpha, []), (Beta, []), (Gamma, [0, 1]));
        var fx = WavesAreValidatedAndAttributedTests.Wiki();
        fx.Pause = new RunPause();
        fx.Checks = Snapshot(Check("slow", Sleep(4), CheckFire.Step, CheckSeverity.Blocking, timeout: 1, matches: "pages/**"));
        var store = new Store();
        var first = new ByStepChatProvider(plan);
        first.Step(Alpha, WavesAreValidatedAndAttributedTests.Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        first.Step(Beta, WavesAreValidatedAndAttributedTests.Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));
        PauseWhileRunning(first, fx.Pause, Alpha, Beta);

        var paused = await fx.RunAsync(fx.Build(first, EngineFixture.Role("developer"), maxParallelSteps: 2, checkpoints: store), "write the pages");

        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());
        Assert.StartsWith("NOT CHECKED", Assert.Single(Checks(paused, "slow (wave 1: steps 1 and 2)")).Summary, StringComparison.Ordinal);
        var checkpoint = Assert.Single(store.Kept);
        Assert.Null(checkpoint.PendingChecks);
        return (fx, store, checkpoint, plan);
    }

    private static async Task<List<WorkEvent>> ResumeWithAStepThatWritesNothingAsync(EngineFixture fx, string plan, RunCheckpoint checkpoint)
    {
        fx.Pause = null;
        var second = new ByStepChatProvider(plan) { Resumed = true }.Step(Gamma, Turn.Says("Nothing left to write."));
        return (await fx.ResumeAsync(fx.Build(second, EngineFixture.Role("developer"), maxParallelSteps: 2, checkpoints: new Store()), checkpoint)).ToList();
    }

    // ── the quick action ────────────────────────────────────────────────────────────────────

    /// <summary>A quick action's one step has its checks at its gate, and its run checks at its end - as a plan's.</summary>
    [Fact]
    public async Task A_quick_action_runs_its_step_checks_at_the_gate_and_its_run_checks_at_the_end()
    {
        using var fx = new EngineFixture
        {
            Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Blocking, matches: "src/**"),
                Check("audit", Failing, CheckFire.Run, CheckSeverity.Advisory))
        };
        var worker = new FakeChatProvider(Turn.Says(Quick), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."),
            Writes("src/app.txt", "v2", "w2"), Turn.Says("Fixed it."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the app");

        Assert.Equal(2, Checks(events, "lint (the step)").Length);
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.ReviewFailed && e.Summary.StartsWith("FAIL (the engine's check 'lint'", StringComparison.Ordinal)));
        Assert.StartsWith("FAIL", Assert.Single(Checks(events, "audit (the run)")).Summary, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.Contains("rejected by an engine check: the engine's check 'lint' failed", events.Last().OutcomeReason(), StringComparison.Ordinal);
    }

    /// <summary>A staged quick action runs no check against the disk: owed when its files are applied, and not Completed.</summary>
    [Fact]
    public async Task A_staged_quick_action_runs_no_check_and_cannot_complete()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", Passing, CheckFire.Step, CheckSeverity.Blocking, matches: "src/**")) };
        var worker = new FakeChatProvider(Turn.Says(Quick), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"),
            settings: new RunSettings(3, "Autonomous", "developer", Staged: true)), "write the app");

        Assert.Contains(events, e => e.Summary.Contains("Step check(s) lint not run: staged: needs the applied files", StringComparison.Ordinal));
        var lint = Assert.Single(Checks(events, "lint ("));
        Assert.Contains("lint (staged, owed when its files are applied)", lint.Summary, StringComparison.Ordinal);
        Assert.StartsWith("NOT CHECKED", lint.Summary, StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── review off ──────────────────────────────────────────────────────────────────────────

    /// <summary>With no reviewer, a blocking check that did not run leaves the step done and not confirmed - never Succeeded.</summary>
    [Fact]
    public async Task With_review_off_a_blocking_check_that_did_not_run_leaves_the_step_unconfirmed()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", Passing, CheckFire.Step, CheckSeverity.Blocking, matches: "src/**")) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."));
        var asks = new PermissionPolicy(PermissionLevel.Execute, ["*"], ["run_command"]);

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), policy: asks), "write the app");

        var end = StepEnd(events, "Write the app");
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Equal("the engine's check 'lint' did not run: not run: needs permission - " + WorkspaceChecks.NotAsked, end.OutcomeReason());
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved && e.Summary.StartsWith("[1] the engine's check 'lint' did not run", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    // ── the engine's own checks never hold ───────────────────────────────────────────────────

    /// <summary>
    /// Only a person's check holds a run back: an engine check (System) - even one made required by some future change -
    /// stays a report, a "but", as RunOutcomeDecision's comment says.
    /// </summary>
    [Fact]
    public void A_required_engine_check_does_not_hold_the_run_back()
    {
        var work = new RunWork([new SettledStep(1, "write", StepOutcomeKind.Succeeded)], Resumable: false);
        var before = RunOutcomeDecision.BeforeChecks(work);
        CriterionResult Engine(CriterionOutcome outcome) => new("No new build errors", "dotnet build", Required: true, outcome, 1, "a new error", CriterionOrigin.System);

        var (failed, reason) = RunOutcomeDecision.Settle(work, before, new RunVerification(SuccessReport.NothingToCheck, null), [Engine(CriterionOutcome.Failed)]);
        Assert.Equal(RunOutcomeKind.Completed, failed);
        Assert.Contains("but check 'No new build errors' failed", reason, StringComparison.Ordinal);

        var (unknown, _) = RunOutcomeDecision.Settle(work, before, new RunVerification(SuccessReport.NothingToCheck, null), [Engine(CriterionOutcome.Unknown)]);
        Assert.Equal(RunOutcomeKind.Completed, unknown);
    }
}
