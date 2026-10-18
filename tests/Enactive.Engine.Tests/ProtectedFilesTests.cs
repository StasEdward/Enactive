namespace Enactive.Engine.Tests;

using System.Diagnostics;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 3.4: files a person protected with a check's <c>protects</c> globs (a project's tests, say). Their hashes are
/// taken with the run's baseline and kept with it; at a step's gate, a protected file the step removed or renamed sends the
/// step back on the gate's budget - no reviewer is asked - and one it edited is shown to the reviewer, with its diff, as
/// protected. A step the planner marked as one the request asks to change protected files is only shown it. Steps side
/// by side are judged at the wave's end, as a blocking check is. Nothing here is about one kind of project: "tests/" is
/// any folder a person said a run must not quietly lose.
/// </summary>
public sealed class ProtectedFilesTests
{
    private const string Protected = "tests/keep.txt";

    private const string OneStep = """{"disposition":"task","title":"tidy","steps":[{"title":"Tidy the project","dependsOn":[]}]}""";

    private const string MarkedStep = """
        {"disposition":"task","title":"tidy","steps":[{"title":"Tidy the project","dependsOn":[],"changesProtected":true}]}
        """;

    private const string Advice = "tests/keep.txt is protected by check 'tests'; restore it or say why the request requires removing it";

    /// <summary>A check that protects tests/** - its own command is about other files, and passes.</summary>
    private static CheckSnapshot Protecting()
        => new([new ResolvedCheck(new CheckDefinition("tests", null, "exit 0", ["src/**"], CheckFire.Step, CheckSeverity.Advisory,
            CheckLimits.DefaultTimeoutSeconds, ["tests/**"]), CheckOrigin.Workspace)], []);

    private static EngineFixture Fixture()
    {
        var fx = new EngineFixture { Checks = Protecting() };
        fx.Decisions.Answer = "allow";   // delete_file always asks; in these runs the person said yes
        fx.Write(Protected, "keep\n");
        return fx;
    }

    private static Turn Deletes(string path, string id) => Turn.Calls1("delete_file", JsonSerializer.Serialize(new { path }), id);

    private static WorkEvent StepEnd(IEnumerable<WorkEvent> events, string title)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains(title, StringComparison.Ordinal));

    private static string PromptOf(FakeChatProvider reviewer)
        => string.Join("\n", reviewer.Requests.SelectMany(r => r.Messages).Select(m => m.Content ?? ""));

    // ── removed or renamed: blocking ──────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS: a step that deletes a protected file is sent back with the protected wording, the reviewer is
    /// never asked, and once the gate's budget is spent the step is rejected by the engine's check.
    /// </summary>
    [Fact]
    public async Task A_step_that_deletes_a_protected_file_is_rejected_by_the_gate_without_a_reviewer()
    {
        using var fx = Fixture();
        var worker = new FakeChatProvider(Turn.Says(OneStep), Deletes(Protected, "d1"), Turn.Says("Removed the old test."),
            Turn.Says("It was not needed."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "tidy the project");

        Assert.Empty(reviewer.Requests);
        var sentBack = events.Where(e => e.Kind == EventKind.ReviewFailed).ToArray();
        Assert.Equal(2, sentBack.Length);
        Assert.All(sentBack, e => Assert.StartsWith("[1] FAIL (the engine's check 'tests' - no reviewer was asked)", e.Summary, StringComparison.Ordinal));
        var end = StepEnd(events, "Tidy the project");
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.Contains(Advice, end.OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal("tests", end.EngineCheckId());
        // The step was told what to do about it, as the engine's check and not a review.
        var told = worker.Requests.Last().Messages.Select(m => m.Content ?? "").Last(m => m.Contains("engine's check", StringComparison.Ordinal));
        Assert.Contains(Advice, told, StringComparison.Ordinal);
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>A rename takes the file from where the run found it as surely as a delete: the same finding.</summary>
    [Fact]
    public async Task A_step_that_renames_a_protected_file_is_rejected_the_same_way()
    {
        using var fx = Fixture();
        var worker = new FakeChatProvider(Turn.Says(OneStep),
            Turn.Calls1("move_file", JsonSerializer.Serialize(new { from = Protected, to = "tests/renamed.txt" }), "m1"),
            Turn.Says("Renamed the test."), Turn.Says("Done."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "tidy the project");

        Assert.Empty(reviewer.Requests);
        var end = StepEnd(events, "Tidy the project");
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.Contains(Advice, end.OutcomeReason(), StringComparison.Ordinal);
    }

    /// <summary>A step that leaves the protected files alone is not touched by any of this - not a line, not an event.</summary>
    [Fact]
    public async Task A_step_that_leaves_protected_files_alone_hears_nothing_of_them()
    {
        using var fx = Fixture();
        var worker = new FakeChatProvider(Turn.Says(OneStep),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = "docs/readme.md", content = "# readme" }), "w1"),
            Turn.Says("Wrote the readme."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "tidy the project");

        Assert.DoesNotContain(Orchestrator.ProtectedFilesTool, PromptOf(reviewer), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Summary.Contains("protected", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── edited: the reviewer's to judge ────────────────────────────────────────────────────

    private static void Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private static void Repository(EngineFixture fx)
    {
        Git(fx.Root, "init", "-q");
        Git(fx.Root, "config", "user.email", "t@example.com");
        Git(fx.Root, "config", "user.name", "t");
        Git(fx.Root, "config", "core.autocrlf", "false");
        Git(fx.Root, "add", "-A");
        Git(fx.Root, "commit", "-q", "-m", "start");
    }

    /// <summary>
    /// A step that edits a protected file is not sent back: the reviewer is shown, as the engine's own evidence, that the
    /// file is protected and what changed in it - and decides.
    /// </summary>
    [Fact]
    public async Task A_step_that_edits_a_protected_file_shows_the_reviewer_its_diff_and_is_not_blocked()
    {
        using var fx = Fixture();
        Repository(fx);
        var worker = new FakeChatProvider(Turn.Says(OneStep),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = Protected, content = "keep\nEDITED-LINE\n" }), "w1"),
            Turn.Says("Updated the test."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "tidy the project");

        var prompt = PromptOf(reviewer);
        Assert.Contains(Orchestrator.ProtectedFilesTool, prompt, StringComparison.Ordinal);
        Assert.Contains("tests/keep.txt (protected by check 'tests'): EDITED", prompt, StringComparison.Ordinal);
        Assert.Contains("+EDITED-LINE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, "Tidy the project").StepOutcome());
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// Orchestrator.ProtectedDiffChars: the diff of an edited protected file is shown to the reviewer as its start and its
    /// end, with the cut said - a rewritten test file would otherwise crowd the step's own evidence out of the review.
    /// </summary>
    [Fact]
    public void A_long_diff_of_a_protected_file_is_shown_in_part_and_says_so()
    {
        var diff = "@@ -1 +1,400 @@\n" + string.Join("\n", Enumerable.Range(1, 400).Select(i => $"+line {i} " + new string('x', 40))) + "\n+LAST-LINE";

        var line = Orchestrator.ProtectedLine(Protected, "tests", "EDITED", diff);

        Assert.StartsWith("- tests/keep.txt (protected by check 'tests'): EDITED", line, StringComparison.Ordinal);
        Assert.True(line.Length < Orchestrator.ProtectedDiffChars + 400, $"{line.Length}");
        Assert.Contains("characters not shown here", line, StringComparison.Ordinal);
        Assert.Contains("+LAST-LINE", line, StringComparison.Ordinal);
    }

    // ── a step the request asks to change them ─────────────────────────────────────────────

    /// <summary>
    /// The planner marked the step as one the request asks to change protected files: the removal is only shown to the
    /// reviewer, who decides - the step is not sent back for it.
    /// </summary>
    [Fact]
    public async Task A_step_marked_to_change_protected_files_has_the_removal_shown_not_blocked()
    {
        using var fx = Fixture();
        var worker = new FakeChatProvider(Turn.Says(MarkedStep), Deletes(Protected, "d1"), Turn.Says("Removed the obsolete test."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "remove the obsolete test");

        var prompt = PromptOf(reviewer);
        Assert.Contains(Orchestrator.ProtectedFilesTool, prompt, StringComparison.Ordinal);
        Assert.Contains("tests/keep.txt (protected by check 'tests'): REMOVED", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, "Tidy the project").StepOutcome());
        Assert.False(fx.Exists(Protected));
    }

    // ── steps side by side ─────────────────────────────────────────────────────────────────

    private const string Alpha = "Alpha writes page a";
    private const string Beta = "Beta writes page b";
    private const string Gamma = "Gamma builds on both";

    /// <summary>
    /// Two steps side by side, one of which deletes a protected file: the finding is not put on the step at its gate - the
    /// workspace is shared - but at the end of the wave, as a blocking check is, where it stops the run.
    /// </summary>
    [Fact]
    public async Task With_parallel_steps_a_removed_protected_file_is_found_at_the_end_of_the_wave()
    {
        using var fx = WavesAreValidatedAndAttributedTests.Wiki();
        fx.Checks = Protecting();
        fx.Decisions.Answer = "allow";
        fx.Write(Protected, "keep\n");
        var provider = new ByStepChatProvider(WavesAreValidatedAndAttributedTests.Plan((Alpha, []), (Beta, []), (Gamma, [0, 1])));
        provider.Step(Alpha, Deletes(Protected, "a1"), Turn.Says("Removed it."));
        provider.Step(Beta, WavesAreValidatedAndAttributedTests.Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));
        provider.Step(Gamma, WavesAreValidatedAndAttributedTests.Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));

        var events = (await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages")).ToList();

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        var found = Assert.Single(events, e => e.Kind == EventKind.CriterionEvaluated
                                              && e.Summary.Contains("tests: protected files (wave 1", StringComparison.Ordinal));
        Assert.StartsWith("FAIL", found.Summary, StringComparison.Ordinal);
        Assert.Contains(Advice, found.Summary, StringComparison.Ordinal);
        Assert.Equal(CheckBoundary.Wave, found.CriterionCheck()!.FiredOn);
        Assert.True(events.IndexOf(found) > events.IndexOf(StepEnd(events, Alpha)) && events.IndexOf(found) > events.IndexOf(StepEnd(events, Beta)));
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, Alpha).StepOutcome());
        Assert.Empty(provider.RequestsFor(Gamma));
        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
    }

    // ── kept with the baseline ──────────────────────────────────────────────────────────────

    /// <summary>Every checkpoint written, as written - the test picks the one it resumes from.</summary>
    private sealed class AllSaves : IRunCheckpointStore
    {
        private readonly List<RunCheckpoint> _saved = [];
        public IReadOnlyList<RunCheckpoint> Saved { get { lock (_saved) return _saved.ToArray(); } }
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (_saved) _saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult(Saved);
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    private const string Look = "Look at the project";
    private const string Remove = "Remove what is obsolete";

    private static string TwoSteps(bool marked) => $$"""
        {"disposition":"task","title":"tidy","steps":[{"title":"{{Look}}","dependsOn":[]},
         {"title":"{{Remove}}","dependsOn":[0]{{(marked ? ",\"changesProtected\":true" : "")}}}]}
        """;

    /// <summary>The checkpoint written once the first step is done, through the store a host keeps them in.</summary>
    private static async Task<RunCheckpoint> AfterTheFirstStepAsync(EngineFixture fx, bool marked)
    {
        var store = new AllSaves();
        var first = new ByStepChatProvider(TwoSteps(marked)).Step(Look, Turn.Says("Looked.")).Step(Remove, Turn.Says("Nothing to remove."));
        await fx.RunAsync(fx.Build(first, EngineFixture.Role("developer"), checkpoints: store), "tidy the project");
        var checkpoint = store.Saved.First(c => c.Steps[0].Status == "Done" && c.Steps[1].Status != "Done");
        // Kept and read back as a host keeps it, so what resumes is what was on disk.
        var disk = new JsonCheckpointStore(fx.Workspace);
        await disk.SaveAsync(checkpoint, default);
        return (await disk.LoadAsync(checkpoint.RunId, default))!;
    }

    /// <summary>
    /// A resumed run judges by the hashes taken at the run's start, kept in its checkpoint - not by the files as they are
    /// when it is resumed: a file added while it was interrupted is not protected, and the one it started with still is.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_keeps_the_protected_files_of_its_baseline()
    {
        using var fx = Fixture();
        var checkpoint = await AfterTheFirstStepAsync(fx, marked: false);

        var kept = Assert.Single(checkpoint.ProtectedFiles!);
        Assert.Equal(Protected, kept.Path);
        Assert.Equal(CheckFolders.HashOf("keep\n"u8.ToArray()), kept.Sha256);
        Assert.Equal(["tests"], kept.CheckIds);

        // Added while the run was interrupted: the baseline never saw it, so removing it is no finding.
        fx.Write("tests/added.txt", "added\n");
        var second = new ByStepChatProvider(TwoSteps(false)) { Resumed = true }.Step(Remove, Deletes("tests/added.txt", "d1"), Turn.Says("Removed it."));
        var resumed = await fx.ResumeAsync(fx.Build(second, EngineFixture.Role("developer")), checkpoint);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(resumed, Remove).StepOutcome());

        // The one the run started with is still protected.
        var third = new ByStepChatProvider(TwoSteps(false)) { Resumed = true }
            .Step(Remove, Deletes(Protected, "d2"), Turn.Says("Removed it."), Turn.Says("Done."));
        var again = await fx.ResumeAsync(fx.Build(third, EngineFixture.Role("developer")), checkpoint);
        var end = StepEnd(again, Remove);
        Assert.Equal(StepOutcomeKind.ReviewRejected, end.StepOutcome());
        Assert.Contains(Advice, end.OutcomeReason(), StringComparison.Ordinal);
    }

    /// <summary>The planner's mark is kept with the step in the checkpoint, and a resumed step is judged by it.</summary>
    [Fact]
    public async Task A_step_marked_to_change_protected_files_keeps_its_mark_through_a_resume()
    {
        using var fx = Fixture();
        var checkpoint = await AfterTheFirstStepAsync(fx, marked: true);

        Assert.False(checkpoint.Steps[0].ChangesProtected);
        Assert.True(checkpoint.Steps[1].ChangesProtected);

        var second = new ByStepChatProvider(TwoSteps(true)) { Resumed = true }.Step(Remove, Deletes(Protected, "d1"), Turn.Says("Removed it."));
        var resumed = await fx.ResumeAsync(fx.Build(second, EngineFixture.Role("developer")), checkpoint);

        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(resumed, Remove).StepOutcome());
        Assert.DoesNotContain(resumed, e => e.Kind == EventKind.ReviewFailed);
    }

    // ── the planner ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The planner reads the mark like readOnly and critical, gives it back when it is asked to correct its plan, and is
    /// told about it only when a check protects files - a prompt for every run would be a sentence about nothing.
    /// </summary>
    [Fact]
    public async Task The_planner_reads_the_mark_keeps_it_through_a_replan_and_is_told_of_it_only_with_protected_files()
    {
        var provider = new FakeChatProvider(Turn.Says(MarkedStep), Turn.Says(OneStep));
        var context = new WorkContext(Guid.NewGuid(), "ws", null, null, null, [], []);
        var planner = new Planner();

        var plan = await planner.PlanAsync("remove the obsolete test", context, provider, "m", default, protectsFiles: true);
        Assert.True(Assert.Single(plan.Plan!.Steps).ChangesProtected);
        Assert.Contains("changesProtected", provider.Requests[0].Messages[0].Content, StringComparison.Ordinal);

        await planner.ReplanAsync("remove the obsolete test", context, plan, "a diagnosis", provider, "m", default, null, false, 10,
            protectsFiles: true);
        var prior = provider.Requests[1].Messages.Single(m => m.Role == ChatRole.Assistant).Content!;
        Assert.Contains("\"changesProtected\":true", prior, StringComparison.Ordinal);

        Assert.DoesNotContain("changesProtected", Planner.SystemPromptFor(null), StringComparison.Ordinal);
        Assert.Contains("changesProtected", Planner.SystemPromptFor(null, protectsFiles: true), StringComparison.Ordinal);
    }

    /// <summary>A quick action has no steps to mark: the plan says it beside its disposition, and its one step is judged by it.</summary>
    [Fact]
    public async Task A_quick_action_marked_to_change_protected_files_has_the_removal_shown_not_blocked()
    {
        using var fx = Fixture();
        var worker = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"remove it","changesProtected":true}"""),
            Deletes(Protected, "d1"), Turn.Says("Removed the obsolete test."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "remove the obsolete test");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.False(fx.Exists(Protected));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// A quick action carried on after a question has no checkpoint: the protected hashes are kept beside the task, as the
    /// build baseline is, and forgotten with it.
    /// </summary>
    [Fact]
    public void The_task_keeps_its_protected_files_beside_its_baseline_and_forgets_them_with_it()
    {
        using var fx = new EngineFixture();
        var store = new BaselineStore(fx.Root);
        var task = Guid.NewGuid();
        Assert.Null(store.LoadProtected(task));

        store.SaveProtected(task, [new ProtectedFile(Protected, "abc", ["tests"])]);
        var back = Assert.Single(store.LoadProtected(task)!);
        Assert.Equal(Protected, back.Path);
        Assert.Equal(["tests"], back.CheckIds);

        store.Forget(task);
        Assert.Null(store.LoadProtected(task));
    }

    /// <summary>The hashes are of the files the enabled checks' globs cover, and of no other - a disabled check protects nothing.</summary>
    [Fact]
    public void The_protected_files_are_the_ones_an_enabled_checks_globs_cover()
    {
        using var fx = new EngineFixture();
        fx.Write(Protected, "keep\n");
        fx.Write("tests/deep/also.txt", "also\n");
        fx.Write("src/app.txt", "app\n");
        fx.Write("docs/off.md", "off\n");
        var off = new ResolvedCheck(new CheckDefinition("docs", null, "exit 0", ["src/**"], CheckFire.Step, CheckSeverity.Advisory,
            10, ["docs/**"]) { Enabled = false }, CheckOrigin.Workspace);
        var snapshot = new CheckSnapshot([.. Protecting().Checks, off], []);

        var taken = ProtectedFiles.Take(fx.Root, snapshot, out var note);

        Assert.Equal(["tests/deep/also.txt", Protected], taken.Select(p => p.Path).Order(StringComparer.Ordinal));
        Assert.Null(note);
        Assert.Empty(ProtectedFiles.Take(fx.Root, null, out _));
    }

    // ── fix round 1 ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A step done for each item does the marked step's work: "remove each obsolete test in this list" has its removals made
    /// by the item steps, and they carry the mark - each removal is shown, none is a loss that sends a step back.
    /// </summary>
    [Fact]
    public async Task The_item_steps_of_a_marked_step_for_each_item_carry_its_mark()
    {
        using var fx = Fixture();
        fx.StepOutputs = true;
        fx.DynamicSteps = true;
        const string plan = """
            {"disposition":"task","title":"tidy","steps":[
             {"title":"find obsolete tests","dependsOn":[],"output":{"tests":{"type":"path[]","description":"the obsolete tests"}}},
             {"title":"remove test","dependsOn":[0],"forEach":{"step":0,"field":"tests"},"changesProtected":true}]}
            """;
        var worker = new FakeChatProvider(Turn.Says(plan),
            Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { tests = new[] { Protected } }), "s0"), Turn.Says("Found one."),
            Deletes(Protected, "d1"), Turn.Says("Removed it."))
        { WhenExhausted = Turn.Says("Done.") };

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "remove the obsolete tests");

        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("remove test: tests/keep.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.DoesNotContain(events, e => e.Summary.Contains("is protected by check", StringComparison.Ordinal));
        Assert.False(fx.Exists(Protected));
    }

    /// <summary>A workspace whose changes cannot be measured: what a step's commands did is not known.</summary>
    private sealed class NoMeasurement : Enactive.Core.Artifacts.IWorkspaceChangesFactory, Enactive.Core.Artifacts.IWorkspaceChanges
    {
        public Enactive.Core.Artifacts.IWorkspaceChanges Create(string root) => this;
        public Task<Enactive.Core.Artifacts.WorkspaceSnapshot?> TakeAsync(CancellationToken ct) => Task.FromResult<Enactive.Core.Artifacts.WorkspaceSnapshot?>(null);
        public Task<IReadOnlySet<string>?> PathsAsync(Enactive.Core.Artifacts.WorkspaceSnapshot snapshot, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>?>(null);
        public Task<IReadOnlyList<Enactive.Core.Artifacts.FileChange>?> CompareAsync(Enactive.Core.Artifacts.WorkspaceSnapshot before,
            Enactive.Core.Artifacts.WorkspaceSnapshot after, CancellationToken ct) => Task.FromResult<IReadOnlyList<Enactive.Core.Artifacts.FileChange>?>(null);
        public void Dispose() { }
    }

    private const string Then = "Then tidy the rest";

    /// <summary>
    /// A removal the request asked for stays released through a resume: the checkpoint keeps it, so a resumed step whose
    /// changes were not measured finds nothing, and an obligation for that file owed at the run's end is no loss either.
    /// </summary>
    [Fact]
    public async Task A_removal_the_request_asked_for_stays_released_through_a_resume()
    {
        using var fx = Fixture();
        fx.WorkspaceChangesOverride = new NoMeasurement();
        var plan = $$"""
            {"disposition":"task","title":"tidy","steps":[{"title":"{{Remove}}","dependsOn":[],"changesProtected":true},
             {"title":"{{Then}}","dependsOn":[0]}]}
            """;
        var store = new AllSaves();
        var first = new ByStepChatProvider(plan).Step(Remove, Deletes(Protected, "d1"), Turn.Says("Removed it.")).Step(Then, Turn.Says("Nothing."));
        await fx.RunAsync(fx.Build(first, EngineFixture.Role("developer"), checkpoints: store), "remove the obsolete test, then tidy");
        var written = store.Saved.First(c => c.Steps[0].Status == "Done" && c.Steps[1].Status != "Done");
        var disk = new JsonCheckpointStore(fx.Workspace);
        await disk.SaveAsync(written, default);
        var checkpoint = (await disk.LoadAsync(written.RunId, default))!;

        Assert.Equal([Protected], checkpoint.ProtectedReleased);

        var second = new ByStepChatProvider(plan) { Resumed = true }
            .Step(Then, Turn.Calls1("run_command", """{"command":"echo tidied"}""", "c1"), Turn.Says("Tidied."));
        var resumed = await fx.ResumeAsync(fx.Build(second, EngineFixture.Role("developer")), checkpoint);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(resumed, Then).StepOutcome());
        Assert.DoesNotContain(resumed, e => e.Kind == EventKind.ReviewFailed);

        // An obligation for the file, owed at the run's end by a step beside the one that removed it: looked for again
        // after the resume, it is released, not lost.
        var owing = checkpoint with { PendingChecks = [new PendingCheck("tests", CheckBoundary.Run, "owed at the run's end") { Protected = [Protected] }] };
        var third = new ByStepChatProvider(plan) { Resumed = true }.Step(Then, Turn.Says("Tidied."));
        var again = await fx.ResumeAsync(fx.Build(third, EngineFixture.Role("developer")), owing);
        var looked = Assert.Single(again, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("tests: protected files (the run)", StringComparison.Ordinal));
        Assert.StartsWith("PASS", looked.Summary, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Completed, again.Last().Outcome());
    }

    /// <summary>
    /// What an earlier attempt was told about the protected files is history once the step is tried again - even when the
    /// new attempt put the file back and there is nothing to say about it now.
    /// </summary>
    [Fact]
    public async Task An_edit_an_earlier_attempt_was_told_of_is_history_once_the_file_is_put_back()
    {
        using var fx = Fixture();
        Repository(fx);
        var worker = new FakeChatProvider(Turn.Says(OneStep),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = Protected, content = "keep\nEDITED-LINE\n" }), "w1"),
            Turn.Says("Updated the test."),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = Protected, content = "keep\n" }), "w2"),
            Turn.Says("Put the test back."));
        var reviewer = new FakeChatProvider(Verdicts.Fail("the test must not change"), Verdicts.Pass());

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "tidy the project");

        Assert.Contains("tests/keep.txt (protected by check 'tests'): EDITED", string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)),
            StringComparison.Ordinal);
        var second = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.DoesNotContain("(protected by check 'tests'): EDITED", second, StringComparison.Ordinal);
        Assert.Contains("HISTORICAL - the protected files were looked at on an earlier attempt", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// Steps one after the other share one journal: the second step's gate makes history only of ITS earlier attempts'
    /// protected-files records - the first step's edit stays on record as the first step's, not "an earlier attempt".
    /// </summary>
    [Fact]
    public async Task A_later_steps_gate_leaves_an_earlier_steps_protected_files_record_as_it_was()
    {
        using var fx = Fixture();
        const string plan = """
            {"disposition":"task","title":"tidy","steps":[{"title":"Update the test","dependsOn":[]},{"title":"Write the app","dependsOn":[0]}]}
            """;
        var worker = new FakeChatProvider(Turn.Says(plan),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = Protected, content = "keep\nEDITED-LINE\n" }), "w1"),
            Turn.Says("Updated the test."),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = "src/app.txt", content = "v1" }), "w2"), Turn.Says("Wrote it."));
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "update the test, then write the app");

        Assert.Equal(2, reviewer.Requests.Count);
        var second = string.Join("\n", reviewer.Requests[1].Messages.Select(m => m.Content));
        Assert.DoesNotContain("HISTORICAL - the protected files were looked at on an earlier attempt", second, StringComparison.Ordinal);
        Assert.Contains("(protected by check 'tests'): EDITED", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step whose changes were not measured is compared with the protected files as IT found them: an edit an earlier
    /// step made (and the review passed) is not shown to its review again as its own.
    /// </summary>
    [Fact]
    public async Task A_step_whose_changes_were_not_measured_is_not_shown_an_earlier_steps_edit()
    {
        using var fx = Fixture();
        fx.WorkspaceChangesOverride = new NoMeasurement();
        const string plan = """
            {"disposition":"task","title":"tidy","steps":[{"title":"Update the test","dependsOn":[]},{"title":"Run the tidy script","dependsOn":[0]}]}
            """;
        var worker = new FakeChatProvider(Turn.Says(plan),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = Protected, content = "keep\nEDITED-LINE\n" }), "w1"),
            Turn.Says("Updated the test."),
            Turn.Calls1("run_command", """{"command":"echo tidied"}""", "c1"), Turn.Says("Ran it."));
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "update the test, then run the tidy script");

        Assert.Contains("(protected by check 'tests'): EDITED", string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)),
            StringComparison.Ordinal);
        // The steps share one journal, so the second review reads the first step's record too - as the first step's. What
        // matters is that the second step's gate found nothing of its own: the edit is on record once, not twice.
        var last = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(last, System.Text.RegularExpressions.Regex.Escape("(protected by check 'tests'): EDITED")));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// A staged step's edit of a protected file is a proposal, not a change on disk - and is shown to the reviewer all the
    /// same, as the difference between the file on disk and what the run proposes.
    /// </summary>
    [Fact]
    public async Task A_staged_steps_edit_of_a_protected_file_shows_the_reviewer_the_proposed_diff()
    {
        using var fx = Fixture();
        var worker = new FakeChatProvider(Turn.Says(OneStep),
            Turn.Calls1("write_file", JsonSerializer.Serialize(new { path = Protected, content = "keep\nEDITED-LINE\n" }), "w1"),
            Turn.Says("Updated the test."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());
        // As a host stages a run: its writes go to the proposals, not to the disk.
        using var proposals = new StagingArtifactStore(fx.Root);

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer,
            artifacts: proposals, settings: new RunSettings(3, "Autonomous", "developer", Staged: true)), "tidy the project");

        var prompt = PromptOf(reviewer);
        Assert.Contains("tests/keep.txt (protected by check 'tests'): EDITED (proposed", prompt, StringComparison.Ordinal);
        Assert.Contains("+ EDITED-LINE", prompt, StringComparison.Ordinal);
        Assert.Equal("keep\n", fx.Read(Protected));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
    }

    /// <summary>
    /// A quick action carried on after a question is composed with the checks as they are now: a file the earlier attempt
    /// hashed under a check that no longer protects it is not protected any more.
    /// </summary>
    [Fact]
    public async Task A_carried_on_run_protects_only_what_its_own_checks_still_protect()
    {
        using var fx = Fixture();
        fx.Write("old/kept.txt", "old\n");
        var task = Guid.NewGuid();
        new BaselineStore(fx.Root).SaveProtected(task,
        [
            new ProtectedFile("old/kept.txt", CheckFolders.HashOf("old\n"u8.ToArray()), ["gone"]),
            new ProtectedFile(Protected, CheckFolders.HashOf("keep\n"u8.ToArray()), ["gone", "tests"])
        ]);
        var narrowed = ProtectedFiles.StillProtected(new BaselineStore(fx.Root).LoadProtected(task)!, Protecting());
        Assert.Equal(["tests"], Assert.Single(narrowed).CheckIds);

        var worker = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"tidy"}"""), Deletes("old/kept.txt", "d1"),
            Turn.Says("Removed the old file."));
        var orchestrator = fx.Build(worker, EngineFixture.Role("developer"));
        var intent = new Enactive.Core.Intents.Intent(task, "remove the old file", Enactive.Core.Intents.IntentSource.CommandBar,
            new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []), DateTimeOffset.UtcNow);
        var events = new List<WorkEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var ev in orchestrator.SubmitIntentAsync(intent, cts.Token)) events.Add(ev);

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        Assert.False(fx.Exists("old/kept.txt"));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>
    /// ProtectedFiles.MaxFiles: a glob over a huge tree is hashed only so far - every run reads every file it covers - and
    /// the run is told which part is not protected, rather than left to think all of it is.
    /// </summary>
    [Fact]
    public void Protected_files_past_the_most_that_are_hashed_say_so()
    {
        using var fx = new EngineFixture();
        for (var i = 0; i <= ProtectedFiles.MaxFiles; i++)
            fx.Write($"tests/t{i:00000}.txt", "x");

        var taken = ProtectedFiles.Take(fx.Root, Protecting(), out var note);

        Assert.Equal(ProtectedFiles.MaxFiles, taken.Count);
        Assert.NotNull(note);
        Assert.Contains(ProtectedFiles.MaxFiles.ToString(), note, StringComparison.Ordinal);
        Assert.Contains("not protected", note, StringComparison.Ordinal);
    }
}
