namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Builds;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Intents;
using Enactive.Workspace;
using Xunit;
using WikiLint = NoNewBuildErrorsTests.WikiLint;

/// <summary>
/// The workspace baseline - build and tests - is taken once, before a task's FIRST attempt, and a
/// run that carries the task on compares against that one.
///
/// <para><b>The defect.</b> A resumed run deliberately did not take the baseline again, and was
/// right not to: the workspace had been worked on, and a baseline taken then would have called the
/// first attempt's errors old. But the first baseline was not kept either, so the resumed run had
/// none, and its "no new build errors" silently disappeared. Since NeedsUser, carrying a run on is
/// the ordinary way a question gets answered, not an accident.</para>
///
/// <para><b>And the half that was missing.</b> Phase 1.6's baseline is build status AND test
/// status. A test that passed before the work and fails after it is the work's; one that fails and
/// was not there before may be exactly what the work was asked to add.</para>
/// </summary>
public sealed class TheBaselineOutlivesTheAttemptTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"edit the wiki"}""";
    private const string TwoSteps = """
        {"disposition":"task","title":"page then tidy",
         "steps":[{"title":"page","dependsOn":[]},{"title":"tidy","dependsOn":[0]}]}
        """;
    private const string BrokenBoth = "ERROR W1 pages/home.page: broken link to /old\nERROR W2 pages/new.page: empty title\n";

    private static readonly Enactive.Core.Workers.Worker Editor = EngineFixture.WorkerWith("write_file", "read_file", "delete_file");

    private static EngineFixture Wiki()
    {
        var fx = new EngineFixture { EcosystemsOverride = [new WikiLint()] };
        fx.Write("wiki.lint", "rules");
        fx.Write("lint-report.txt", NoNewBuildErrorsTests.OneBrokenLink + "\n");
        fx.Write("draft.md", "an old draft");
        return fx;
    }

    private static WorkEvent Check(IEnumerable<WorkEvent> events, string name)
        => Assert.Single(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains(name, StringComparison.Ordinal));

    /// <summary>
    /// THE ONE THAT MATTERS. The first attempt adds an error and stops at a question; the run carried
    /// on from its checkpoint - through the real checkpoint store, so through JSON - still names that
    /// error as new. Taken again, the baseline would have called it old.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_compares_against_the_baseline_from_before_its_first_attempt()
    {
        using var fx = Wiki();
        var checkpoints = new JsonCheckpointStore(fx.Workspace);
        var ledger = new DecisionLedger(fx.Root);

        var first = await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1("write_file", """{"path":"pages/new.page","content":""}""", "w1"),
            Turn.Calls1("write_file", $$"""{"path":"lint-report.txt","content":{{System.Text.Json.JsonSerializer.Serialize(BrokenBoth)}}}""", "w2"),
            Turn.Says("page added"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("tidied")),
            Editor, checkpoints: checkpoints, decisions: new ParkingDecisionHandler()), "add a page, then tidy");
        Assert.Equal(RunOutcomeKind.NeedsUser, first.Last().Outcome());

        var question = Assert.Single(ledger.Pending());
        ledger.Answer(question.TaskId, question.RequestId, "allow");
        var checkpoint = await ParkedRuns.CheckpointForAsync(checkpoints, question.TaskId, default);
        Assert.NotNull(checkpoint!.Baseline);

        var resumed = await fx.ResumeAsync(fx.Build(new FakeChatProvider(
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"), Turn.Says("tidied")),
            Editor, checkpoints: checkpoints, decisions: new ParkingDecisionHandler()), checkpoint);

        Assert.Contains(resumed, e => e.Summary.Contains("(kept from before the first attempt)", StringComparison.Ordinal));
        var check = Check(resumed, BuildRegression.Name);
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        Assert.Contains("W2 in pages/new.page: empty title", check.Summary, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fx.Root, BaselineStore.Folder))
                     && Directory.EnumerateFiles(Path.Combine(fx.Root, BaselineStore.Folder)).Any());   // an ended task keeps none
    }

    /// <summary>A quick action has no checkpoint; started again under the same task after its question, it finds the baseline beside the task.</summary>
    [Fact]
    public async Task A_quick_action_started_again_after_its_question_keeps_its_baseline()
    {
        using var fx = Wiki();
        var ledger = new DecisionLedger(fx.Root);
        var taskId = Guid.NewGuid();
        Turn[] Work() =>
        [
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"pages/new.page","content":""}""", "w1"),
            Turn.Calls1("write_file", $$"""{"path":"lint-report.txt","content":{{System.Text.Json.JsonSerializer.Serialize(BrokenBoth)}}}""", "w2"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"),
            Turn.Says("done")
        ];

        var first = await Submit(fx, fx.Build(new FakeChatProvider(Work()), Editor, decisions: new ParkingDecisionHandler()), taskId);
        Assert.Equal(RunOutcomeKind.NeedsUser, first.Last().Outcome());
        var question = Assert.Single(ledger.Pending());
        ledger.Answer(taskId, question.RequestId, "allow");

        var second = await Submit(fx, fx.Build(new FakeChatProvider(Work()), Editor, decisions: new ParkingDecisionHandler()), taskId);

        Assert.Equal(RunOutcomeKind.Completed, second.Last().Outcome());
        Assert.Contains("W2 in pages/new.page", Check(second, BuildRegression.Name).Summary, StringComparison.Ordinal);
    }

    // ── test status ──────────────────────────────────────────────────────────────────────

    /// <summary>A link check that passed before the work and fails after it is the work's.</summary>
    [Fact]
    public async Task A_test_that_passed_before_the_work_and_fails_now_is_named()
    {
        using var fx = Wiki();
        fx.Write("links.txt", "PASS home->about\nPASS home->faq\n");

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"pages/about.page","content":"moved"}""", "w1"),
            Turn.Calls1("write_file", """{"path":"links.txt","content":"FAIL home->about\nPASS home->faq\n"}""", "w2"),
            Turn.Says("moved the page")), Editor), "move the about page");

        Assert.Contains(events, e => e.Summary.StartsWith("Tests before the work (wikilint, links): exit 0, 2 passed, 0 failed", StringComparison.Ordinal));
        var check = Check(events, BuildRegression.TestName);
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        Assert.Contains("1 test(s) passed before the work and fail now: home->about", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>A failing test the run before did not have is reported, not held against the run - it may be what was asked for.</summary>
    [Fact]
    public async Task A_new_failing_test_is_reported_and_not_counted()
    {
        using var fx = Wiki();
        fx.Write("links.txt", "PASS home->about\n");

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"pages/faq.page","content":"new"}""", "w1"),
            Turn.Calls1("write_file", """{"path":"links.txt","content":"PASS home->about\nFAIL faq->missing\n"}""", "w2"),
            Turn.Says("added the faq, with a check that shows its link is missing")), Editor), "add a faq page");

        var check = Check(events, BuildRegression.TestName);
        Assert.StartsWith("PASS", check.Summary, StringComparison.Ordinal);
        Assert.Contains("did not have fail now - not counted against it", check.Summary, StringComparison.Ordinal);
        Assert.Contains("faq->missing", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>A build that failed before the work stops its tests before they run: they are not taken, and the run says why.</summary>
    [Fact]
    public async Task Tests_are_not_baselined_over_a_build_that_did_not_pass()
    {
        using var fx = Wiki();
        fx.Write("links.txt", "PASS home->about\n");
        fx.Write("wiki.lint", "rules");
        var lint = new FailingBuild();
        fx.EcosystemsOverride = [lint];

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(QuickAction), Turn.Says("nothing to do")), Editor), "look");

        Assert.Contains(events, e => e.Summary.Contains("Tests before the work (wikilint, links) was not taken: the build did not pass before the work", StringComparison.Ordinal));
    }

    /// <summary>The wiki linter, with a build that exits 1.</summary>
    private sealed class FailingBuild : IEcosystem
    {
        private readonly WikiLint _inner = new();
        public string Name => _inner.Name;
        public EcosystemTargets? Detect(string workspaceRoot) => _inner.Detect(workspaceRoot);
        public bool Owns(string relativePath) => _inner.Owns(relativePath);
        public string BuildCommand(string target) => "type no-such-report.txt";
        public string TestCommand(string target) => _inner.TestCommand(target);
        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot) => _inner.ParseDiagnostics(output, workspaceRoot);
        public TestRunReport? ParseTests(string output) => _inner.ParseTests(output);
    }

    private static async Task<List<WorkEvent>> Submit(EngineFixture fx, Orchestrator engine, Guid taskId)
    {
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var events = new List<WorkEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var ev in engine.SubmitIntentAsync(new Intent(taskId, "edit the wiki", IntentSource.CommandBar, context,
                           DateTimeOffset.UtcNow), cts.Token))
            events.Add(ev);
        return events;
    }
}

/// <summary>
/// What <c>dotnet test</c> prints, read - fitted to output captured on this machine on 2026-09-28
/// (xUnit: one test passing, two failing, one skipped), at normal and at default verbosity.
/// </summary>
public sealed class DotnetTestOutputTests
{
    private const string Normal = """
          Probe.Tests -> C:\probe\bin\Debug\net10.0\Probe.Tests.dll
        Test run for C:\probe\bin\Debug\net10.0\Probe.Tests.dll (.NETCoreApp,Version=v10.0)
        [xUnit.net 00:00:00.28]     Probe.Sums.Subtracts_wrongly [FAIL]
          Failed Probe.Sums.Subtracts_wrongly [52 ms]
          Error Message:
           Assert.Equal() Failure: Values differ
          Stack Trace:
             at Probe.Sums.Subtracts_wrongly() in C:\probe\T.cs:line 6
          Passed Probe.Sums.Adds_two_numbers [< 1 ms]
          Skipped Probe.Sums.Divides [1 ms]
          Passed Probe.Sums.Is_positive(n: 2) [< 1 ms]
          Failed Probe.Sums.Is_positive(n: 1) [< 1 ms]

        Test Run Failed.
        Total tests: 5
             Passed: 2
             Failed: 2
            Skipped: 1
         Total time: 0.6774 Seconds
        """;

    private const string Minimal = """
          Failed Probe.Sums.Subtracts_wrongly [52 ms]
          Error Message:
           Assert.Equal() Failure: Values differ
          Skipped Probe.Sums.Divides [1 ms]
          Failed Probe.Sums.Is_positive(n: 1) [< 1 ms]

        Failed!  - Failed:     2, Passed:     2, Skipped:     1, Total:     5, Duration: 22 ms - Probe.Tests.dll (net10.0)
        """;

    [Fact]
    public void Normal_verbosity_names_every_test_and_its_totals()
    {
        var run = new DotnetEcosystem().ParseTests(Normal)!;

        Assert.Equal(5, run.Cases.Count);
        Assert.Contains(new TestCaseResult("Probe.Sums.Is_positive(n: 1)", TestVerdict.Failed), run.Cases);
        Assert.Contains(new TestCaseResult("Probe.Sums.Adds_two_numbers", TestVerdict.Passed), run.Cases);
        Assert.Equal(new TestRunSummary(2, 2, 1, 5), run.Summary);
    }

    [Fact]
    public void Default_verbosity_gives_the_failures_and_the_one_line_summary()
    {
        var run = new DotnetEcosystem().ParseTests(Minimal)!;

        Assert.Equal(3, run.Cases.Count);
        Assert.Equal(new TestRunSummary(2, 2, 1, 5), run.Summary);
    }

    [Fact]
    public void The_engine_asks_for_normal_verbosity_so_passing_tests_are_named()
        => Assert.Contains("verbosity=normal", new DotnetEcosystem().TestCommand("T.csproj"), StringComparison.Ordinal);

    [Fact]
    public void A_build_error_is_not_a_test_run()
        => Assert.Null(new DotnetEcosystem().ParseTests("C:\\p\\A.cs(3,1): error CS0103: The name 'x' does not exist\n"));

    /// <summary>Only what was seen twice is compared: a test missing from either run is not guessed about.</summary>
    [Fact]
    public void Regressions_are_tests_seen_passing_before_and_failing_now()
    {
        var before = new TestRunReport([new("a", TestVerdict.Passed), new("b", TestVerdict.Failed), new("c", TestVerdict.Passed)], null);
        var now = new TestRunReport([new("a", TestVerdict.Failed), new("b", TestVerdict.Failed), new("d", TestVerdict.Failed)], null);

        Assert.Equal(["a"], now.Regressions(before));
        Assert.Equal(["d"], now.FailingAndNew(before));
    }
}
