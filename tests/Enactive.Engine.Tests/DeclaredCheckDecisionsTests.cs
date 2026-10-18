namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Builds;
using Enactive.Core.Checks;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Plan Phase 3.2, without a run around it: what a declared check's result does to a run's outcome, which checks a change
/// is owed, and how a rejection by one is worded.
/// </summary>
public sealed class DeclaredCheckDecisionsTests
{
    private static RunWork Done() => new([new SettledStep(1, "write", StepOutcomeKind.Succeeded)], Resumable: false);

    private static CriterionResult Declared(CriterionOutcome outcome, bool blocking, string detail = "it said no")
        => new("lint (the run)", "npm run lint", blocking, outcome, outcome == CriterionOutcome.Unknown ? null : outcome == CriterionOutcome.Passed ? 0 : 1,
            outcome == CriterionOutcome.Passed ? null : detail, CriterionOrigin.Declared);

    private static CriterionResult Engines(CriterionOutcome outcome)
        => new("No new build errors", "dotnet build", false, outcome, 1, "a new error", CriterionOrigin.System);

    private static (RunOutcomeKind Outcome, string? Reason) Settle(RunWork work, params CriterionResult[] finalChecks)
    {
        var before = RunOutcomeDecision.BeforeChecks(work);
        return RunOutcomeDecision.Settle(work, before, before.Verify ? new RunVerification(SuccessReport.NothingToCheck, null) : null, finalChecks);
    }

    [Fact]
    public void A_blocking_declared_check_that_failed_fails_the_run_and_is_named()
    {
        var (outcome, reason) = Settle(Done(), Declared(CriterionOutcome.Failed, blocking: true));

        Assert.Equal(RunOutcomeKind.Failed, outcome);
        Assert.Equal("Not complete - check 'lint (the run)' failed: it said no", reason);
    }

    [Fact]
    public void A_blocking_declared_check_that_did_not_run_keeps_the_run_from_completed()
    {
        var (outcome, reason) = Settle(Done(), Declared(CriterionOutcome.Unknown, blocking: true, "not run: needs permission"));

        Assert.Equal(RunOutcomeKind.Incomplete, outcome);
        Assert.Contains("check 'lint (the run)' unknown: not run: needs permission", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_advisory_declared_check_that_failed_is_a_but_as_the_engines_own_are()
    {
        var (outcome, reason) = Settle(Done(), Declared(CriterionOutcome.Failed, blocking: false), Engines(CriterionOutcome.Failed));

        Assert.Equal(RunOutcomeKind.Completed, outcome);
        Assert.Contains("but check 'lint (the run)' failed: it said no; check 'No new build errors' failed", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_passing_declared_check_never_promotes_a_run_its_steps_did_not_finish()
    {
        var work = new RunWork([new SettledStep(1, "write", StepOutcomeKind.DoneUnverified, "no verdict")], Resumable: false);

        Assert.Equal(RunOutcomeKind.Incomplete, Settle(work, Declared(CriterionOutcome.Passed, blocking: true)).Outcome);
    }

    [Fact]
    public void A_rejection_by_a_declared_check_is_counted_and_worded_apart()
    {
        var before = RunOutcomeDecision.BeforeChecks(new RunWork(
        [
            new SettledStep(1, "write", StepOutcomeKind.ReviewRejected, "the engine's check 'lint' failed (exit 1)", Cause: OutcomeCause.CheckGateRejected),
            new SettledStep(2, "fix", StepOutcomeKind.ReviewRejected, "wrong", Cause: OutcomeCause.ReviewRejected)
        ], Resumable: false));

        Assert.Equal("the engine's check 'lint' failed (exit 1) (1 step(s) rejected by the reviewer; 1 step(s) rejected by an engine check)", before.Reason);
        Assert.Equal("rejected by an engine check", RunOutcomeDecision.Word(StepOutcomeKind.ReviewRejected, OutcomeCause.CheckGateRejected));
        Assert.Equal("Rejected by an engine check", ItemReport.Status(new StepRecord(StepOutcomeKind.ReviewRejected,
            OutcomeCause.CheckGateRejected, "x", ResultStanding.Rejected, null)));
        Assert.Equal("Rejected by the engine's check 'lint' — failed",
            RunOutcomeWords.StepActivity(StepOutcomeKind.ReviewRejected, "failed", byEngineCheck: true, checkId: "lint"));
        Assert.Equal("Rejected by the engine's coverage check — covered 2 of 12",
            RunOutcomeWords.StepActivity(StepOutcomeKind.ReviewRejected, "covered 2 of 12", byEngineCheck: true));
    }

    [Fact]
    public void The_card_says_which_check_sent_the_step_back()
    {
        var feed = new RunFeed(null);
        feed.Apply(new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, EventKind.StepStarted, "[1/1] write", "{\"step\":1}"));
        feed.Apply(new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, EventKind.ReviewFailed,
            "[1] FAIL (the engine's check 'lint' - no reviewer was asked): failed", WorkEventPayload.EngineCheckPayload(1, "lint")));

        Assert.Equal("The engine's check 'lint' sent it back…", feed.Cards.Last().Activity);
        Assert.True(new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, EventKind.ReviewFailed, "x",
            WorkEventPayload.EngineCheckPayload(1, "lint")).ByEngineCheck());
    }

    // ── what a change is owed ──────────────────────────────────────────────────────────────

    private static ResolvedCheck Check(string id, string? command, CheckFire fire, CheckOrigin origin = CheckOrigin.Workspace,
        CheckOrigin? shadows = null, bool enabled = true, string[]? ecosystems = null, params string[] matches)
        => new(new CheckDefinition(id, null, command, matches, fire, CheckSeverity.Blocking, 30, null) { Enabled = enabled }, origin, shadows)
        { Ecosystems = ecosystems };

    private sealed class Pages : IEcosystem
    {
        public string Name => "pages";
        public EcosystemTargets? Detect(string workspaceRoot) => null;
        public bool Owns(string relativePath) => relativePath.EndsWith(".page", StringComparison.OrdinalIgnoreCase);
        public string BuildCommand(string target) => "build";
        public string TestCommand(string target) => "test";
        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot) => [];
    }

    [Fact]
    public void What_fires_is_enabled_and_runnable_and_not_the_engines_own_build_nobody_raised()
    {
        var snapshot = new CheckSnapshot(
        [
            Check("lint", "npm run lint", CheckFire.Step, matches: "src/**"),
            Check("off", "npm run off", CheckFire.Step, enabled: false, matches: "src/**"),
            Check("audit", "npm audit", CheckFire.Run),
            Check("build", null, CheckFire.Run, CheckOrigin.Implicit, ecosystems: ["pages"]),
            Check("test", null, CheckFire.Step, CheckOrigin.Workspace, CheckOrigin.Implicit, ecosystems: ["pages"])
        ], []);

        Assert.Equal(["lint", "test"], WorkspaceChecks.Firing(snapshot, CheckFire.Step).Select(c => c.Id));
        Assert.Equal(["audit"], WorkspaceChecks.Firing(snapshot, CheckFire.Run).Select(c => c.Id));
        Assert.Empty(WorkspaceChecks.Firing(null, CheckFire.Run));
    }

    [Fact]
    public void A_change_is_owed_a_check_by_its_globs_or_by_what_its_ecosystem_owns()
    {
        var lint = Check("lint", "npm run lint", CheckFire.Step, matches: "src/**/*.ts");
        var build = Check("build", null, CheckFire.Step, CheckOrigin.Workspace, CheckOrigin.Implicit, ecosystems: ["pages"]);

        Assert.True(WorkspaceChecks.Covers(lint, ["docs/a.md", "src/app/main.ts"], []));
        Assert.False(WorkspaceChecks.Covers(lint, ["docs/a.md"], []));
        Assert.True(WorkspaceChecks.Covers(build, ["wiki/home.page"], [new Pages()]));
        Assert.False(WorkspaceChecks.Covers(build, ["wiki/home.md"], [new Pages()]));
    }

    private static ExecutedAction Action(string tool, WorkspaceEffect effect, params string[] changed)
        => new(DateTimeOffset.UtcNow, 1, tool, "{}", ActionOutcome.Succeeded, "", effect, changed.Length == 0 ? null : changed, null, false,
            ToolCallOrigin.Native, null);

    [Fact]
    public void What_a_step_changed_is_measured_and_recorded_and_an_unmeasured_command_is_said()
    {
        var root = Path.Combine(Path.GetTempPath(), "ws");
        var recorded = WorkspaceChecks.Of(["src/a.ts"], [Action("write_file", WorkspaceEffect.Changed, Path.Combine(root, "src", "b.ts"))],
            ["notes/c.md"], root, _ => false);
        Assert.Equal(["notes/c.md", "src/a.ts", "src/b.ts"], recorded.Paths.Order());
        Assert.Null(recorded.Unmeasured);

        // A command that recorded no paths: with a measurement, it is in what was measured; without one, nothing is known.
        Assert.Null(WorkspaceChecks.Of([], [Action("run_command", WorkspaceEffect.Unknown)], [], root, t => t == "run_command").Unmeasured);
        Assert.Equal(WorkspaceChecks.Unmeasured,
            WorkspaceChecks.Of(null, [Action("run_command", WorkspaceEffect.Unknown)], [], root, t => t == "run_command").Unmeasured);
        // A read changes nothing, measured or not.
        Assert.Null(WorkspaceChecks.Of(null, [Action("read_file", WorkspaceEffect.None)], [], root, _ => false).Unmeasured);
    }
}
