namespace Enactive.Engine.Tests;

using System.Collections.Concurrent;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Builds;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Workers;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 3.2: the checks a person declared for a workspace are run where they are owed - at a step's gate, at the
/// end of a wave, at the run's end - through the one evaluator every criterion goes through. A blocking one that fails
/// rejects the step without asking the reviewer; an advisory one is evidence for the reviewer; one that did not run is
/// said, and never a pass. Deliberately not about one kind of project: a "lint" that is any command, pages, flags.
/// </summary>
public sealed class DeclaredChecksAreRunTests
{
    /// <summary>A command that fails and prints what it found - the same on cmd and sh.</summary>
    private const string Failing = "echo LINT-ERROR-42 && exit 1";
    private const string Passing = "exit 0";

    private const string OneStep = """{"disposition":"task","title":"app","steps":[{"title":"Write the app","dependsOn":[]}]}""";

    private static ResolvedCheck Check(string id, string command, CheckFire fire, CheckSeverity severity, params string[] matches)
        => new(new CheckDefinition(id, null, command, matches, fire, severity, CheckLimits.DefaultTimeoutSeconds, null), CheckOrigin.Workspace);

    private static CheckSnapshot Snapshot(params ResolvedCheck[] checks) => new(checks, []);

    private static Turn Writes(string path, string content, string id)
        => Turn.Calls1("write_file", JsonSerializer.Serialize(new { path, content }), id);

    private static WorkEvent[] Checks(IEnumerable<WorkEvent> events, string label)
        => events.Where(e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains(label, StringComparison.Ordinal)).ToArray();

    private static WorkEvent StepEnd(IEnumerable<WorkEvent> events, string title)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains(title, StringComparison.Ordinal));

    // ── at the step's gate ────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS: a blocking check fails on what the step changed - the step is sent back with the check's own
    /// output, on the gate's budget, and rejected once that is spent. The reviewer is never asked, and everything a person
    /// reads says the engine's check did it.
    /// </summary>
    [Fact]
    public async Task A_blocking_step_check_that_fails_rejects_the_step_without_asking_the_reviewer()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Blocking, "src/**")) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."),
            Writes("src/app.txt", "v2", "w2"), Turn.Says("Fixed it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the app");

        Assert.Empty(reviewer.Requests);
        var end = StepEnd(events, "Write the app");
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.StartsWith("the engine's check 'lint' failed (exit 1)", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("REJECTED BY THE ENGINE'S CHECK 'lint'", end.Summary, StringComparison.Ordinal);
        // Two attempts: the gate's budget with review on is as many retries as the review has (1).
        var sentBack = events.Where(e => e.Kind == EventKind.ReviewFailed).ToArray();
        Assert.Equal(2, sentBack.Length);
        Assert.All(sentBack, e => Assert.StartsWith("[1] FAIL (the engine's check 'lint' - no reviewer was asked)", e.Summary, StringComparison.Ordinal));
        Assert.Equal(2, Checks(events, "lint (step 1)").Length);
        // Told what the check printed, as the engine's check and not a review.
        var told = worker.Requests.Last().Messages.Select(m => m.Content ?? "").Last(m => m.Contains("engine's check", StringComparison.Ordinal));
        Assert.Contains("LINT-ERROR-42", told, StringComparison.Ordinal);
        Assert.DoesNotContain("A reviewer rejected", told, StringComparison.Ordinal);
        // The card, and the run's own line.
        var feed = new RunFeed(null);
        foreach (var ev in events) feed.Apply(ev);
        Assert.Contains(feed.Cards, c => c.Activity.StartsWith("Rejected by the engine's check 'lint' — the engine's check 'lint' failed", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.Contains("[1] Write the app - rejected by an engine check: the engine's check 'lint' failed", events.Last().OutcomeReason(), StringComparison.Ordinal);
    }

    /// <summary>An advisory check that fails is the engine's evidence for the reviewer - who decides, here that the step is done.</summary>
    [Fact]
    public async Task An_advisory_step_check_that_fails_is_evidence_and_the_reviewer_decides()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Advisory, "src/**")) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the app");

        var asked = string.Join("\n", Assert.Single(reviewer.Requests).Messages.Select(m => m.Content));
        Assert.Contains("lint (advisory", asked, StringComparison.Ordinal);
        Assert.Contains("LINT-ERROR-42", asked, StringComparison.Ordinal);
        Assert.StartsWith("FAIL", Assert.Single(Checks(events, "lint (step 1)")).Summary, StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, "Write the app").StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// Steps one after the other share one journal: the second step's gate makes history only of ITS earlier attempts'
    /// check records, never of the first step's - which its review still reads, as the first step's, not as "an earlier
    /// attempt of this step".
    /// </summary>
    [Fact]
    public async Task A_later_steps_gate_leaves_an_earlier_steps_check_record_as_it_was()
    {
        const string twoSteps = """
            {"disposition":"task","title":"app","steps":[{"title":"Write the app","dependsOn":[]},{"title":"Write the docs","dependsOn":[0]}]}
            """;
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Advisory, "src/**")) };
        var worker = new FakeChatProvider(Turn.Says(twoSteps), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."),
            Writes("src/docs.txt", "d1", "w2"), Turn.Says("Wrote the docs."));
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the app and its docs");

        Assert.Equal(2, reviewer.Requests.Count);
        Assert.Single(Checks(events, "lint (step 1)"));
        Assert.Single(Checks(events, "lint (step 2)"));
        var second = string.Join("\n", reviewer.Requests[1].Messages.Select(m => m.Content));
        Assert.DoesNotContain("HISTORICAL - the checks were run on an earlier attempt of this step", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blocking check the policy would ask about is not run on a question: it is NOT CHECKED, said as "not run: needs
    /// permission", and the step is done, never confirmed - however the reviewer found it.
    /// </summary>
    [Fact]
    public async Task A_blocking_step_check_the_policy_would_ask_about_is_not_run_and_the_step_is_not_confirmed()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", Passing, CheckFire.Step, CheckSeverity.Blocking, "src/**")) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());
        var asks = new PermissionPolicy(PermissionLevel.Execute, ["*"], ["run_command"]);

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), policy: asks, router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the app");

        var check = Assert.Single(Checks(events, "lint (step 1)"));
        Assert.StartsWith("NOT CHECKED", check.Summary, StringComparison.Ordinal);
        Assert.Contains("not run: needs permission", check.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(fx.Decisions.Requests, r => r.FullDetail?.Contains("lint", StringComparison.Ordinal) == true);
        var end = StepEnd(events, "Write the app");
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Contains("the engine's check 'lint' did not run: not run: needs permission", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>A check whose globs miss what the step changed is not owed by it, and does not run; one whose globs hit, does.</summary>
    [Fact]
    public async Task A_check_whose_matches_miss_what_the_step_changed_does_not_run()
    {
        using var fx = new EngineFixture
        {
            Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Blocking, "src/**"),
                Check("spelling", Passing, CheckFire.Step, CheckSeverity.Blocking, "docs/*.md"))
        };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("docs/readme.md", "# app", "w1"), Turn.Says("Wrote the docs."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "document the app");

        Assert.Empty(Checks(events, "lint ("));
        Assert.StartsWith("PASS", Assert.Single(Checks(events, "spelling (step 1)")).Summary, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>A workspace whose changes cannot be measured: nothing is ever measured.</summary>
    private sealed class NoMeasurement : IWorkspaceChangesFactory, IWorkspaceChanges
    {
        public IWorkspaceChanges Create(string root) => this;
        public Task<WorkspaceSnapshot?> TakeAsync(CancellationToken ct) => Task.FromResult<WorkspaceSnapshot?>(null);
        public Task<IReadOnlySet<string>?> PathsAsync(WorkspaceSnapshot snapshot, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>?>(null);
        public Task<IReadOnlyList<FileChange>?> CompareAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FileChange>?>(null);
        public void Dispose() { }
    }

    /// <summary>
    /// A step that ran a command whose changes were not measured may have changed anything: every step check runs, and
    /// the run says why - unknown attribution never means "matches nothing".
    /// </summary>
    [Fact]
    public async Task A_step_that_ran_an_unmeasured_command_owes_every_step_check_and_says_why()
    {
        using var fx = new EngineFixture
        {
            WorkspaceChangesOverride = new NoMeasurement(),
            Checks = Snapshot(Check("lint", Passing, CheckFire.Step, CheckSeverity.Blocking, "src/**"))
        };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Turn.Calls1("run_command", """{"command":"echo generated"}""", "c1"),
            Turn.Says("Generated it."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "generate the app");

        Assert.StartsWith("PASS", Assert.Single(Checks(events, "lint (step 1)")).Summary, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary.Contains("the step ran commands whose changes were not measured, so every step check runs",
            StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// The engine's own build, raised by a workspace manifest to block at the step: a step that breaks the build is
    /// rejected with no reviewer asked - and the check is run through the ecosystem, with no command of its own.
    /// </summary>
    [Fact]
    public async Task The_implicit_build_raised_to_block_at_the_step_rejects_a_step_that_breaks_the_build()
    {
        using var fx = new EngineFixture { EcosystemsOverride = [new Flags()] };
        fx.Write("flags.proj", "flags");
        fx.Write(Path.Combine(".enactive", "checks", "build.json"), """{"severity":"Blocking","fireOn":"Step"}""");
        fx.Checks = new CheckStore(fx.Root, fx.Root + "-no-global", [new Flags()]).Snapshot();
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("broken.src", "x", "w1"), Turn.Says("Wrote it."),
            Writes("broken.src", "y", "w2"), Turn.Says("Fixed it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the app");

        Assert.Empty(reviewer.Requests);
        var end = StepEnd(events, "Write the app");
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.StartsWith("the engine's check 'build' failed", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("BUILD-BROKEN", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
    }

    /// <summary>A build that breaks when a file named broken.src is there; it owns *.src.</summary>
    private sealed class Flags : IEcosystem
    {
        public string Name => "flags";

        public EcosystemTargets? Detect(string workspaceRoot)
            => File.Exists(Path.Combine(workspaceRoot, "flags.proj")) ? new(Name, ["flags.proj"], []) : null;

        public bool Owns(string relativePath) => relativePath.EndsWith(".src", StringComparison.OrdinalIgnoreCase);

        public string BuildCommand(string target) => OperatingSystem.IsWindows()
            ? "if exist broken.src (echo BUILD-BROKEN && exit 1) else (exit 0)"
            : "if [ -f broken.src ]; then echo BUILD-BROKEN; exit 1; fi";

        public string TestCommand(string target) => "exit 0";

        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot) => [];
    }

    // ── steps side by side ──────────────────────────────────────────────────────────────────

    private const string Alpha = "Alpha writes page a";
    private const string Beta = "Beta writes page b";
    private const string Gamma = "Gamma builds on both";

    /// <summary>
    /// Two steps side by side, a blocking check both owe: it runs once, after both have finished - at the end of the
    /// wave, and is put on the wave, never on one of its steps. Failed, it stops the run as a wave that could not be put
    /// back does: what builds on the wave never starts.
    /// </summary>
    [Fact]
    public async Task With_parallel_steps_a_blocking_check_runs_at_the_end_of_the_wave_and_is_put_on_the_wave()
    {
        using var fx = WavesAreValidatedAndAttributedTests.Wiki();
        fx.Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Blocking, "pages/**"));
        var provider = new ByStepChatProvider(WavesAreValidatedAndAttributedTests.Plan((Alpha, []), (Beta, []), (Gamma, [0, 1])));
        provider.Step(Alpha, WavesAreValidatedAndAttributedTests.Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, WavesAreValidatedAndAttributedTests.Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));
        provider.Step(Gamma, WavesAreValidatedAndAttributedTests.Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));

        var events = (await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages")).ToList();

        var check = Assert.Single(Checks(events, "lint ("));
        Assert.Contains("lint (wave 1: steps 1 and 2)", check.Summary, StringComparison.Ordinal);
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        var ranAt = events.IndexOf(check);
        Assert.True(ranAt > events.IndexOf(StepEnd(events, Alpha)) && ranAt > events.IndexOf(StepEnd(events, Beta)), events.Text());
        // Neither step is blamed: both stand as done.
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, Alpha).StepOutcome());
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, Beta).StepOutcome());
        Assert.Empty(provider.RequestsFor(Gamma));
        Assert.Equal(StepOutcomeKind.Skipped, StepEnd(events, Gamma).StepOutcome());
        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.Contains("lint (wave 1: steps 1 and 2)", events.Last().OutcomeReason(), StringComparison.Ordinal);
    }

    /// <summary>Where no wave is kept, the boundary is the run's end - and the failure is the run's, not a step's.</summary>
    [Fact]
    public async Task With_parallel_steps_and_no_waves_a_blocking_check_runs_at_the_run_end()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Blocking, "src/**")) };
        var provider = new ByStepChatProvider(WavesAreValidatedAndAttributedTests.Plan((Alpha, []), (Beta, [])));
        provider.Step(Alpha, Writes("src/a.txt", "A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, Writes("src/b.txt", "B", "b1"), Turn.Says("Wrote b."));

        var events = (await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write them")).ToList();

        var check = Assert.Single(Checks(events, "lint ("));
        Assert.Contains("lint (the run)", check.Summary, StringComparison.Ordinal);
        Assert.True(events.IndexOf(check) > events.IndexOf(StepEnd(events, Alpha)) && events.IndexOf(check) > events.IndexOf(StepEnd(events, Beta)));
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, Alpha).StepOutcome());
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, Beta).StepOutcome());
        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
    }

    // ── pause and resume ───────────────────────────────────────────────────────────────────

    private sealed class Store : IRunCheckpointStore
    {
        private readonly Dictionary<Guid, RunCheckpoint> _kept = new();

        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct)
        {
            lock (_kept) _kept[checkpoint.RunId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct)
        {
            lock (_kept) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(_kept.Values.ToArray());
        }

        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) return Task.FromResult(_kept.GetValueOrDefault(runId));
        }

        public Task DeleteAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) _kept.Remove(runId);
            return Task.CompletedTask;
        }

        public IReadOnlyList<RunCheckpoint> Kept
        {
            get { lock (_kept) return _kept.Values.ToArray(); }
        }
    }

    /// <summary>The pause arrives while both steps are running, and neither answers before it does.</summary>
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
    /// Paused before the boundary a check is owed at: the checkpoint keeps the obligation, and the resumed run runs the
    /// check at its own end - failed there, the run is not Completed. Resumed where the check cannot run, it is not
    /// checked and still not Completed: a pending check never turns into a pass.
    /// </summary>
    [Fact]
    public async Task A_pause_before_the_boundary_keeps_the_owed_check_and_a_pending_check_never_turns_green()
    {
        var plan = WavesAreValidatedAndAttributedTests.Plan((Alpha, []), (Beta, []), (Gamma, [0, 1]));
        using var fx = new EngineFixture
        {
            Pause = new RunPause(),
            Checks = Snapshot(Check("lint", Failing, CheckFire.Step, CheckSeverity.Blocking, "src/**"))
        };
        var store = new Store();
        var first = new ByStepChatProvider(plan);
        first.Step(Alpha, Writes("src/a.txt", "A", "a1"), Turn.Says("Wrote a."));
        first.Step(Beta, Writes("src/b.txt", "B", "b1"), Turn.Says("Wrote b."));
        PauseWhileRunning(first, fx.Pause, Alpha, Beta);

        var paused = await fx.RunAsync(fx.Build(first, EngineFixture.Role("developer"), maxParallelSteps: 2, checkpoints: store), "write them");

        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());
        Assert.Empty(Checks(paused, "lint ("));                                     // owed, not run, and not passed
        var checkpoint = Assert.Single(store.Kept);
        var owed = Assert.Single(checkpoint.PendingChecks!);
        Assert.Equal("lint", owed.CheckId);
        Assert.Equal(CheckBoundary.Run, owed.DeferredTo);

        // Resumed, and the check runs at the resumed run's end - and fails there.
        fx.Pause = null;
        var second = new ByStepChatProvider(plan) { Resumed = true }.Step(Gamma, Turn.Says("Nothing to add."));
        var resumed = await fx.ResumeAsync(fx.Build(second, EngineFixture.Role("developer"), maxParallelSteps: 2, checkpoints: new Store()), checkpoint);

        Assert.StartsWith("FAIL", Assert.Single(Checks(resumed, "lint (the run)")).Summary, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Failed, resumed.Last().Outcome());

        // Resumed where the check cannot run: not checked, and the run still not Completed.
        var third = new ByStepChatProvider(plan) { Resumed = true }.Step(Gamma, Turn.Says("Nothing to add."));
        var asks = new PermissionPolicy(PermissionLevel.Execute, ["*"], ["run_command"]);
        var unchecked_ = await fx.ResumeAsync(fx.Build(third, EngineFixture.Role("developer"), policy: asks, maxParallelSteps: 2,
            checkpoints: new Store()), checkpoint);

        Assert.StartsWith("NOT CHECKED", Assert.Single(Checks(unchecked_, "lint (the run)")).Summary, StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, unchecked_.Last().Outcome());
    }

    // ── staged ───────────────────────────────────────────────────────────────────────────

    private static string[] Pages(int n) => Enumerable.Range(1, n).Select(i => $"wiki/p{i:00}.md").ToArray();

    private const string CoveredPlan = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages","evidence":"mentioned"}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """;

    /// <summary>
    /// A staged run's writes are not on disk, so no command check runs against it: a required one is not checked, with
    /// the staged reason, and the run is not Completed. The engine's coverage count reads no disk and is still decided.
    /// </summary>
    [Fact]
    public async Task A_staged_run_runs_no_command_check_and_cannot_complete_but_its_coverage_is_still_decided()
    {
        var pages = Pages(3);
        using var fx = new EngineFixture
        {
            StepOutputs = true, TypedCriteria = true,
            Checks = Snapshot(Check("verify", Passing, CheckFire.Run, CheckSeverity.Blocking),
                Check("lint", Passing, CheckFire.Step, CheckSeverity.Blocking, "wiki/**"))
        };
        foreach (var page in pages) fx.Write(page, $"# {page}\n");
        var worker = new FakeChatProvider([Turn.Says(CoveredPlan),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { pages }), "s1"), Turn.Says("Found them."),
            .. pages.Select(p => Turn.Calls1("read_file", JsonSerializer.Serialize(new { path = p }), "r-" + p)),
            Writes("wiki/p01.md", "# fixed\n", "w1"),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes = pages.ToDictionary(p => p, p => $"{p} is fine") }), "s2"),
            Turn.Says("Reviewed all three.")]);

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"),
            settings: new RunSettings(3, "Autonomous", "developer", Staged: true)), "review every wiki page");

        var verify = Assert.Single(Checks(events, "verify (the run)"));
        Assert.StartsWith("NOT CHECKED", verify.Summary, StringComparison.Ordinal);
        Assert.Contains("staged: needs the applied files", verify.Summary, StringComparison.Ordinal);
        var lint = Assert.Single(Checks(events, "lint ("));
        Assert.StartsWith("NOT CHECKED", lint.Summary, StringComparison.Ordinal);
        Assert.Contains("staged: needs the applied files", lint.Summary, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary.StartsWith("[2] The engine's coverage check passed", StringComparison.Ordinal));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains("staged: needs the applied files", events.Last().OutcomeReason(), StringComparison.Ordinal);
    }

    // ── at the run's end ──────────────────────────────────────────────────────────────────

    /// <summary>A blocking run check that fails keeps the run from Completed; an advisory one is a "but", as the engine's own are.</summary>
    [Theory]
    [InlineData(CheckSeverity.Blocking)]
    [InlineData(CheckSeverity.Advisory)]
    public async Task A_run_check_that_fails_holds_the_run_back_when_blocking_and_is_a_but_when_advisory(CheckSeverity severity)
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("audit", Failing, CheckFire.Run, severity)) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the app");

        Assert.StartsWith("FAIL", Assert.Single(Checks(events, "audit (the run)")).Summary, StringComparison.Ordinal);
        if (severity == CheckSeverity.Blocking)
        {
            Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
            Assert.Contains("check 'audit (the run)' failed", events.Last().OutcomeReason(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
            Assert.Contains("but check 'audit (the run)' failed", events.Last().OutcomeReason(), StringComparison.Ordinal);
        }
    }

    // ── the snapshot ──────────────────────────────────────────────────────────────────────

    private const string TwoStepPlan = """
        {"disposition":"task","title":"two steps",
         "steps":[{"title":"first","dependsOn":[]},{"title":"second","dependsOn":[0]}]}
        """;

    private const string Audit = """{"id":"audit","command":"echo AUDIT-SAID-NO && exit 1","fireOn":"Run","severity":"Blocking"}""";

    private static string ManifestPath(EngineFixture fx) => fx.PathOf(Path.Combine(".enactive", "checks", "audit.json"));

    private static ComposedEngine Engine(FakeChatProvider provider, string globalChecks)
    {
        var models = new ModelResolver();
        var settings = new AppSettings { Engine = new() { ProposeChecks = false, ReviewRetries = 0, SuccessRetries = 0 } };
        var worker = EngineFixture.WorkerWith();
        return new ComposedEngine(new SingleProviderFactory(provider), new StaticWorkerProvider([worker], worker.Id),
            new ModelRouter(models), models, new ToolRegistry(EngineFixture.ShippedTools()), [], settings, new LogHub(),
            new SessionApprovals())
        {
            Planner = new Planner(checksAuditEnabled: false),
            Approvals = new ApprovalStore(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-permissions.json")),
            GlobalChecks = globalChecks
        };
    }

    private static async Task<List<WorkEvent>> EventsOf(ComposedRun composed)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<WorkEvent>();
        await foreach (var ev in composed.Events(cts.Token))
            events.Add(ev);
        return events;
    }

    /// <summary>
    /// A check made to pass after the run was composed, and then deleted while it was paused, still judges the run and its
    /// resume as it was composed: the snapshot runs, not the files.
    /// </summary>
    [Fact]
    public async Task A_check_edited_or_deleted_after_compose_judges_the_run_and_its_resume_as_composed()
    {
        using var fx = new EngineFixture();
        fx.Write(Path.Combine(".enactive", "checks", "audit.json"), Audit);
        var engine = Engine(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") }, fx.Root + "-no-global");
        var pause = new RunPause();
        pause.Request();

        try
        {
            List<WorkEvent> paused;
            await using (var composed = await RunComposer.ComposeAsync(engine,
                             new RunRequest(fx.Workspace, "do two things", IntentSource.CommandBar) { Defaults = new(3), Pause = pause },
                             new ScriptedDecisionHandler("allow"), CancellationToken.None))
            {
                fx.Write(Path.Combine(".enactive", "checks", "audit.json"), Audit.Replace("echo AUDIT-SAID-NO && exit 1", "exit 0"));
                paused = await EventsOf(composed);
            }
            Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());
            var kept = Assert.Single(await new JsonCheckpointStore(fx.Workspace).LoadAllAsync(CancellationToken.None));

            File.Delete(ManifestPath(fx));
            List<WorkEvent> resumed;
            await using (var composed = await RunComposer.ComposeAsync(engine,
                             new RunRequest(fx.Workspace, kept.Request, IntentSource.CommandBar, Resume: kept) { Defaults = new(3) },
                             new ScriptedDecisionHandler("allow"), CancellationToken.None))
                resumed = await EventsOf(composed);

            var audit = Assert.Single(Checks(resumed, "audit (the run)"));
            Assert.StartsWith("FAIL", audit.Summary, StringComparison.Ordinal);
            Assert.Contains("AUDIT-SAID-NO", audit.Summary, StringComparison.Ordinal);
            Assert.Equal(RunOutcomeKind.Failed, resumed.Last().Outcome());

            // And a run composed and then given an edited manifest, without a pause, is judged as composed too.
            fx.Write(Path.Combine(".enactive", "checks", "audit.json"), Audit);
            var again = Engine(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") }, fx.Root + "-no-global");
            List<WorkEvent> whole;
            await using (var composed = await RunComposer.ComposeAsync(again,
                             new RunRequest(fx.Workspace, "do two things", IntentSource.CommandBar) { Defaults = new(3) },
                             new ScriptedDecisionHandler("allow"), CancellationToken.None))
            {
                File.Delete(ManifestPath(fx));
                whole = await EventsOf(composed);
            }
            Assert.StartsWith("FAIL", Assert.Single(Checks(whole, "audit (the run)")).Summary, StringComparison.Ordinal);
            Assert.Equal(RunOutcomeKind.Failed, whole.Last().Outcome());
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
        }
    }
}
