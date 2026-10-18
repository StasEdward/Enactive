namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Builds;
using Enactive.Core.Checks;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A blocking step check that already failed on the workspace as the step found it is not put on the step as its own
/// failure (StepStartCheck). Bench build-error, 2026-10-18: the workspace did not build before the run, a manifest raised
/// the implicit build to block each step, and the step that added what it was asked for correctly was failed on the old
/// error - and went after it in a file the request forbade it to touch. Deliberately not a compiler: the "build" here is a
/// notes linter whose errors are lines of a report.
/// </summary>
public sealed class AStepCheckThatFailedBeforeTheStepTests
{
    private const string OneStep = """{"disposition":"task","title":"notes","steps":[{"title":"Write the page","dependsOn":[]}]}""";

    private const string TwoSteps = """
        {"disposition":"task","title":"notes","steps":[{"title":"Write page a","dependsOn":[]},{"title":"Write page b","dependsOn":[0]}]}
        """;

    private const string OldError = "ERROR N1 pages/old.page: broken link to /gone";

    /// <summary>
    /// A notes linter, as an ecosystem: present where notes.proj is, owns .page files, and "builds" by printing its report -
    /// lines of <c>ERROR code path: message</c> - failing while fails.flag is there.
    /// </summary>
    private sealed class Notes(bool readsErrors = true) : IEcosystem
    {
        public string Name => "notes";

        public EcosystemTargets? Detect(string workspaceRoot)
            => File.Exists(Path.Combine(workspaceRoot, "notes.proj")) ? new(Name, ["notes.proj"], []) : null;

        public bool Owns(string relativePath) => relativePath.EndsWith(".page", StringComparison.OrdinalIgnoreCase);

        public string BuildCommand(string target) => OperatingSystem.IsWindows()
            ? "type report.txt & if exist fails.flag (exit 1) else (exit 0)"
            : "cat report.txt; if [ -f fails.flag ]; then exit 1; fi";

        public string TestCommand(string target) => "exit 0";

        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot)
            => !readsErrors ? []
                : output.Split('\n').Select(l => l.Trim())
                    .Where(l => l.StartsWith("ERROR ", StringComparison.Ordinal))
                    .Select(l =>
                    {
                        var parts = l.Split(' ', 3);
                        var colon = parts[2].IndexOf(':');
                        return new BuildDiagnostic(Name, parts[2][..colon], parts[1], DiagnosticSeverity.Error, parts[2][(colon + 1)..].Trim());
                    }).ToArray();
    }

    /// <summary>A workspace whose build fails before the run (or, <paramref name="failing"/> false, passes), with the build raised to block each step.</summary>
    private static EngineFixture Workspace(bool failing = true, bool readsErrors = true)
    {
        var ecosystem = new Notes(readsErrors);
        var fx = new EngineFixture { EcosystemsOverride = [ecosystem] };
        fx.Write("notes.proj", "notes");
        fx.Write("report.txt", failing ? OldError + "\n" : "all good\n");
        if (failing) fx.Write("fails.flag", "");
        fx.Write(Path.Combine(".enactive", "checks", "build.json"), """{"severity":"Blocking","fireOn":"Step"}""");
        fx.Checks = new CheckStore(fx.Root, fx.Root + "-no-global", [ecosystem]).Snapshot();
        return fx;
    }

    private static Turn Writes(string path, string content, string id)
        => Turn.Calls1("write_file", JsonSerializer.Serialize(new { path, content }), id);

    private static WorkEvent[] Checks(IEnumerable<WorkEvent> events, string label)
        => events.Where(e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains(label, StringComparison.Ordinal)).ToArray();

    private static WorkEvent StepEnd(IEnumerable<WorkEvent> events, string title)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains(title, StringComparison.Ordinal));

    private static string ToldAfterTheCheck(FakeChatProvider worker)
        => worker.Requests.Last().Messages.Select(m => m.Content ?? "").Last(m => m.Contains("engine's check", StringComparison.Ordinal));

    // ── failed before, and after ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS: the build failed before the run, the step adds a page and leaves the old error as it found it.
    /// The raised build fails after the step with the same error alone: not the step's - it is not sent back, the review
    /// is asked, and the check's record says the failure was there before it began.
    /// </summary>
    [Fact]
    public async Task A_check_that_failed_before_the_step_with_the_same_errors_does_not_send_it_back()
    {
        using var fx = Workspace();
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("pages/new.page", "# New", "w1"), Turn.Says("Wrote the page."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the new page");

        var check = Assert.Single(Checks(events, "build (step 1)"));
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        Assert.Contains("Failed before this step too, and nothing in it is new", check.Summary, StringComparison.Ordinal);
        Assert.Contains("no error that was not there before this step", check.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        var asked = string.Join("\n", Assert.Single(reviewer.Requests).Messages.Select(m => m.Content));
        Assert.Contains("Failed before this step too", asked, StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, "Write the page").StepOutcome());
    }

    /// <summary>
    /// The next step's start is the first step's end, not the run's: its check is held against what the first step's
    /// gate found - the baseline no longer describes it, as page a has changed since.
    /// </summary>
    [Fact]
    public async Task A_later_step_is_held_against_the_check_an_earlier_step_ran_on_the_workspace_it_found()
    {
        using var fx = Workspace();
        var worker = new FakeChatProvider(Turn.Says(TwoSteps), Writes("pages/a.page", "# A", "w1"), Turn.Says("Wrote a."),
            Writes("pages/b.page", "# B", "w2"), Turn.Says("Wrote b."));
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write pages a and b");

        Assert.Contains("Failed before this step too", Assert.Single(Checks(events, "build (step 2)")).Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, "Write page a").StepOutcome());
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, "Write page b").StepOutcome());
    }

    /// <summary>
    /// Failed before and after, and the step added an error of its own: it still blocks - and the step is told what is
    /// new, and that the error that was there before it began is not its to fix.
    /// </summary>
    [Fact]
    public async Task A_step_that_adds_an_error_to_a_failing_check_is_still_sent_back_and_told_what_is_new()
    {
        using var fx = Workspace();
        var worse = OldError + "\nERROR N2 pages/new.page: empty title\n";
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("pages/new.page", "draft", "w1"), Writes("report.txt", worse, "w2"),
            Turn.Says("Wrote the page."), Writes("report.txt", OldError + "\n", "w3"), Turn.Says("Gave it a title."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the new page");

        var sentBack = events.First(e => e.Kind == EventKind.ReviewFailed);
        Assert.StartsWith("[1] FAIL (the engine's check 'build' - no reviewer was asked)", sentBack.Summary, StringComparison.Ordinal);
        var told = ToldAfterTheCheck(worker);
        Assert.Contains("1 error(s) not in the build before this step - N2 in pages/new.page", told, StringComparison.Ordinal);
        Assert.Contains("a failure that was there before it began is not", told, StringComparison.Ordinal);
        // Put right, it is the old error alone again: the second attempt stands.
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, "Write the page").StepOutcome());
    }

    /// <summary>Passed before the step and fails after it: the step's own failure, as always - and it is told the check passed before it.</summary>
    [Fact]
    public async Task A_check_that_passed_before_the_step_and_fails_after_it_still_blocks()
    {
        using var fx = Workspace(failing: false);
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("pages/new.page", "# New", "w1"), Writes("fails.flag", "", "w2"),
            Writes("report.txt", "ERROR N2 pages/new.page: empty title\n", "w3"), Turn.Says("Wrote the page."));
        worker.WhenExhausted = Turn.Says("Done.");
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the new page");

        Assert.Empty(reviewer.Requests);
        var end = StepEnd(events, "Write the page");
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.StartsWith("the engine's check 'build' failed", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains("it passed before this step", ToldAfterTheCheck(worker), StringComparison.Ordinal);
    }

    /// <summary>
    /// Failed before and after, and nothing it prints reads as errors to compare: whether the step made it worse cannot be
    /// told. It is not sent back for a failure that was there before it began, and not confirmed by a check that said
    /// nothing about it - done, not confirmed, however the review found it.
    /// </summary>
    [Fact]
    public async Task A_check_that_failed_before_with_nothing_to_compare_leaves_the_step_done_and_not_confirmed()
    {
        using var fx = Workspace(readsErrors: false);
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("pages/new.page", "# New", "w1"), Turn.Says("Wrote the page."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the new page");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.Single(reviewer.Requests);
        Assert.Contains("whether this step made it worse cannot be told", Assert.Single(Checks(events, "build (step 1)")).Summary,
            StringComparison.Ordinal);
        var end = StepEnd(events, "Write the page");
        Assert.Equal(StepOutcomeKind.DoneUnverified, end.StepOutcome());
        Assert.Contains("the engine's check 'build' failed before this step too", end.OutcomeReason(), StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── when it cannot be told ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A check of its own command, on the run's first step: no result of it on the workspace the step found exists, and it
    /// is not run again only to find out. Today's behaviour stands - the step is sent back and rejected - and the result
    /// says how it stood before was not known.
    /// </summary>
    [Fact]
    public async Task A_check_with_no_earlier_result_keeps_todays_behaviour_and_says_it_was_not_known()
    {
        using var fx = new EngineFixture
        {
            Checks = new CheckSnapshot([new ResolvedCheck(new CheckDefinition("lint", null, "echo LINT-ERROR-42 && exit 1", ["src/**"],
                CheckFire.Step, CheckSeverity.Blocking, CheckLimits.DefaultTimeoutSeconds, null), CheckOrigin.Workspace)], [])
        };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."),
            Writes("src/app.txt", "v2", "w2"), Turn.Says("Fixed it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the app");

        Assert.Empty(reviewer.Requests);
        Assert.Equal(StepOutcomeKind.ReviewRejected, StepEnd(events, "Write the page").StepOutcome());
        Assert.All(Checks(events, "lint (step 1)"), c => Assert.Contains("Whether it failed before this step too is not known: the run "
            + "has no result of it on the workspace as this step found it", c.Summary, StringComparison.Ordinal));
    }

    /// <summary>A workspace that could not be measured: the step's start is not known, today's behaviour stands, and the result says why.</summary>
    [Fact]
    public async Task A_step_whose_start_was_not_measured_keeps_todays_behaviour_and_says_why()
    {
        using var fx = Workspace();
        fx.WorkspaceChangesOverride = new NoMeasurement();
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("pages/new.page", "# New", "w1"), Turn.Says("Wrote the page."));
        worker.WhenExhausted = Turn.Says("Done.");
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the new page");

        Assert.Equal(StepOutcomeKind.ReviewRejected, StepEnd(events, "Write the page").StepOutcome());
        Assert.Contains("Whether it failed before this step too is not known: the workspace was not measured when the step began",
            Checks(events, "build (step 1)").First().Summary, StringComparison.Ordinal);
    }

    private sealed class NoMeasurement : IWorkspaceChangesFactory, IWorkspaceChanges
    {
        public IWorkspaceChanges Create(string root) => this;
        public Task<WorkspaceSnapshot?> TakeAsync(CancellationToken ct) => Task.FromResult<WorkspaceSnapshot?>(null);
        public Task<IReadOnlySet<string>?> PathsAsync(WorkspaceSnapshot snapshot, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>?>(null);
        public Task<IReadOnlyList<FileChange>?> CompareAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FileChange>?>(null);
        public void Dispose() { }
    }

    // ── a command's own output ─────────────────────────────────────────────────────────────

    private static StepStartCheck.Part Plain(CriterionOutcome outcome, int? exit, string output)
        => new(null, null, BaselineKind.Build, new CriterionResult("lint (step 2)", "npm run lint", true, outcome, exit, null) { Output = output });

    /// <summary>
    /// A check of its own command has no reading the engine knows: it is the same failure only when it prints exactly what
    /// it printed before; anything else cannot be compared. One that passed before is the step's.
    /// </summary>
    [Fact]
    public void A_commands_failure_is_the_same_only_when_it_prints_the_same()
    {
        var after = Plain(CriterionOutcome.Failed, 1, "src/a.ts: 3 problems\r\n");
        StepStartCheck.Judgement Against(StepStartCheck.Part before)
            => StepStartCheck.Judge([after], p => StepStartCheck.Matching([before], p), Path.GetTempPath());

        Assert.Equal(StepStartCheck.Standing.Theirs, Against(Plain(CriterionOutcome.Failed, 1, "src/a.ts: 3 problems\n")).Standing);
        Assert.Equal(StepStartCheck.Standing.Inconclusive, Against(Plain(CriterionOutcome.Failed, 1, "src/a.ts: 4 problems\n")).Standing);
        Assert.Equal(StepStartCheck.Standing.Inconclusive, Against(Plain(CriterionOutcome.Failed, 2, "src/a.ts: 3 problems\n")).Standing);
        Assert.Equal(StepStartCheck.Standing.Ours, Against(Plain(CriterionOutcome.Passed, 0, "")).Standing);
        Assert.Equal(StepStartCheck.Standing.NotKnown, Against(Plain(CriterionOutcome.Unknown, null, "")).Standing);
        Assert.Equal(StepStartCheck.Standing.NotKnown, StepStartCheck.Judge([after], _ => null, Path.GetTempPath()).Standing);
    }
}
