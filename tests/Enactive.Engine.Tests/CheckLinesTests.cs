namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Application.Projections;
using Enactive.Core.Checks;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// The checks a person declared, on the cards that show a run (Plan Phase 3.5): the ones that fired for a step on its card,
/// with PASS / FAIL / NOT RUN, why, and the file that holds the whole output - and the ones that fired at a wave, at the run's
/// end or are owed at apply on the run's own list. One fold (RunFeed) and one projection (CheckLines) for the window and the
/// service, live and reopened. Results are made as the engine makes them: a CriterionResult, its sentence
/// (CriterionResult.Describe) and its payload (WorkEventPayload.CriterionPayload).
/// </summary>
public sealed class CheckLinesTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent Step(EventKind kind, int step, string summary, string? payload = null)
        => Ev(kind, summary, payload ?? $"{{\"step\":{step}}}");

    /// <summary>A declared check's result as RunScope.Criterion publishes it: its sentence and its payload, from the result.</summary>
    private static WorkEvent Checked(CheckFiring firing, CriterionOutcome outcome, int? exit, string command = "npm run lint",
        string? detail = null, bool required = true, string? summary = null)
    {
        var result = new CriterionResult($"{firing.CheckId} (somewhere)", command, required, outcome, exit, detail) { Check = firing };
        return Ev(EventKind.CriterionEvaluated, summary ?? result.Describe(), WorkEventPayload.CriterionPayload(result));
    }

    private static readonly WorkEvent[] TwoSteps =
    [
        Ev(EventKind.PlanCreated, "Docs — 2 steps: write | link", WorkEventPayload.PlanPayload("Docs", ["write", "link"])),
        Step(EventKind.StepStarted, 1, "[1/2] write"),
    ];

    private static RunFeed Fold(IEnumerable<WorkEvent> events)
    {
        var feed = new RunFeed("docs");
        foreach (var ev in events)
            feed.Apply(ev);
        return feed;
    }

    [Fact]
    public void A_step_check_is_on_its_step_s_card_with_its_outcome_and_its_file()
    {
        var feed = Fold([.. TwoSteps,
            Checked(new CheckFiring("lint", CheckBoundary.Step) { Step = 1, DetailPath = ".enactive/checks/runs/x/step-1-lint.txt" },
                CriterionOutcome.Failed, 2, detail: "3 problems")]);

        var line = Assert.Single(CheckLines.Of(feed.Cards[0].Checks));
        Assert.Empty(feed.Cards[1].Checks);
        Assert.Empty(feed.RunChecks);
        Assert.Equal(("lint", CheckVerdict.Fail, "FAIL"), (line.CheckId, line.Verdict, line.Word));
        Assert.Equal("at the step's end", line.Where);
        Assert.Equal("exit 2", line.Reason);
        Assert.Equal(".enactive/checks/runs/x/step-1-lint.txt", line.DetailPath);
        Assert.Equal("FAIL lint — at the step's end — exit 2", line.Text);
    }

    [Fact]
    public void A_check_that_could_not_run_says_it_needs_permission()
    {
        var feed = Fold([.. TwoSteps,
            Checked(new CheckFiring("lint", CheckBoundary.Step) { Step = 1 }, CriterionOutcome.Unknown, null,
                detail: WorkspaceChecks.NeedsPermission + " - " + WorkspaceChecks.NotAsked)]);

        var line = Assert.Single(CheckLines.Of(feed.Cards[0].Checks));
        Assert.Equal((CheckVerdict.NotRun, "NOT RUN"), (line.Verdict, line.Word));
        Assert.Equal("not run: needs permission", line.Reason);
        Assert.Null(line.DetailPath);
    }

    /// <summary>
    /// The reason is the payload's, not a line of the sentence: a command with a line break in it put the command's second
    /// line where the reason was read from.
    /// </summary>
    [Fact]
    public void A_command_with_a_line_break_does_not_move_the_reason()
    {
        var feed = Fold([.. TwoSteps,
            Checked(new CheckFiring("lint", CheckBoundary.Step) { Step = 1 }, CriterionOutcome.Unknown, null, command: "npm run lint\n--fix",
                detail: WorkspaceChecks.NeedsPermission + " - " + WorkspaceChecks.NotAsked)]);

        Assert.Equal("not run: needs permission", Assert.Single(CheckLines.Of(feed.Cards[0].Checks)).Reason);
    }

    /// <summary>A result recorded before the payload kept its reason is read from its sentence, as it was then.</summary>
    [Fact]
    public void A_result_recorded_before_the_reason_was_kept_reads_it_from_the_sentence()
    {
        var firing = new CheckFiring("lint", CheckBoundary.Step) { Step = 1 };
        var old = new CriterionResult("lint (step 1)", "npm run lint", true, CriterionOutcome.Unknown, null,
            WorkspaceChecks.NeedsPermission + " - " + WorkspaceChecks.NotAsked);
        var feed = Fold([.. TwoSteps,
            Ev(EventKind.CriterionEvaluated, old.Describe(), WorkEventPayload.CriterionPayload(old.Name, "Unknown", true, null, "Declared", firing))]);

        Assert.Equal("not run: needs permission", Assert.Single(CheckLines.Of(feed.Cards[0].Checks)).Reason);
    }

    /// <summary>A step sent back runs its checks again at its next gate: each result says which attempt it judged.</summary>
    [Fact]
    public void A_check_run_again_on_a_later_attempt_says_which()
    {
        var feed = Fold([.. TwoSteps,
            Checked(new CheckFiring("lint", CheckBoundary.Step) { Step = 1 }, CriterionOutcome.Failed, 1),
            Checked(new CheckFiring("lint", CheckBoundary.Step) { Step = 1, Attempt = 2 }, CriterionOutcome.Passed, 0)]);

        Assert.Equal(["at the step's end", "at the step's end (attempt 2)"], CheckLines.Of(feed.Cards[0].Checks).Select(l => l.Where));
    }

    /// <summary>
    /// A write check fires after every turn that writes what it covers: the card shows the latest per check, and links only
    /// it - its detail file is written over each turn, so an earlier event's link would open a later turn's output.
    /// </summary>
    [Fact]
    public void Write_checks_are_folded_to_the_latest_on_the_step_they_fired_in()
    {
        WorkEvent Wrote(CriterionOutcome outcome, int exit, string path)
            => Checked(new CheckFiring("spell", CheckBoundary.Write) { Step = 1, DetailPath = path }, outcome, exit, "cspell .",
                required: false,
                summary: $"Write-time check 'spell': {(outcome == CriterionOutcome.Passed ? "PASS" : "FAIL")} (exit {exit}) - run after writing a.md.");

        var feed = Fold([.. TwoSteps,
            Wrote(CriterionOutcome.Failed, 1, "w1.txt"), Wrote(CriterionOutcome.Failed, 1, "w2.txt"), Wrote(CriterionOutcome.Passed, 0, "w3.txt")]);

        var line = Assert.Single(CheckLines.Of(feed.Cards[0].Checks));
        Assert.Equal(CheckVerdict.Pass, line.Verdict);
        Assert.Equal("after a write (the latest of 3)", line.Where);
        Assert.Equal("w3.txt", line.DetailPath);
    }

    [Fact]
    public void A_write_check_that_was_not_run_says_why()
    {
        var feed = Fold([.. TwoSteps,
            Checked(new CheckFiring("spell", CheckBoundary.Write) { Step = 1 }, CriterionOutcome.Unknown, null, "cspell .",
                detail: "not run: needs permission - the policy does not allow this command without asking", required: false,
                summary: "Write-time check 'spell': NOT CHECKED - run after writing a.md.")]);

        Assert.Equal("not run: needs permission", Assert.Single(CheckLines.Of(feed.Cards[0].Checks)).Reason);
    }

    [Fact]
    public void Wave_run_and_apply_checks_are_on_the_run_s_list_and_say_where_they_were_deferred()
    {
        var feed = Fold([.. TwoSteps,
            Checked(new CheckFiring("lint", CheckBoundary.Wave) { Wave = 2, Step = 1, Deferred = "steps ran side by side" },
                CriterionOutcome.Failed, 1),
            Checked(new CheckFiring("audit", CheckBoundary.Run), CriterionOutcome.Passed, 0, "npm audit"),
            Checked(new CheckFiring("types", CheckBoundary.Run) { Deferred = "steps ran side by side" }, CriterionOutcome.Passed, 0, "tsc"),
            Checked(new CheckFiring("tests", CheckBoundary.Apply) { Deferred = WorkspaceChecks.Staged }, CriterionOutcome.Unknown, null,
                "npm test", detail: WorkspaceChecks.StagedDetail)]);

        Assert.All(feed.Cards, c => Assert.Empty(c.Checks));
        var lines = CheckLines.Of(feed.RunChecks);
        Assert.Equal(["lint", "audit", "types", "tests"], lines.Select(l => l.CheckId));
        // Where it was deferred to, said once: "wave 2 — deferred to wave 2" said it twice.
        Assert.Equal("FAIL lint — deferred to wave 2 — exit 1", lines[0].Text);
        Assert.Equal("PASS audit — at the run's end", lines[1].Text);
        Assert.Equal("PASS types — deferred to the run's end", lines[2].Text);
        Assert.Equal("NOT RUN tests — on apply — staged: needs the applied files", lines[3].Text);
    }

    /// <summary>A quick action's step check has no step number: it is on the one card the run has.</summary>
    [Fact]
    public void A_quick_action_s_step_check_is_on_its_card()
    {
        var feed = Fold([
            Ev(EventKind.Routed, "Quick action: tidy", WorkEventPayload.QuickActionPayload("tidy")),
            Ev(EventKind.ToolInvoked, "write_file", WorkEventPayload.ToolPayload("write_file")),
            Checked(new CheckFiring("lint", CheckBoundary.Step), CriterionOutcome.Passed, 0)]);

        Assert.Equal("lint", Assert.Single(Assert.Single(feed.Cards).Checks).Firing.CheckId);
    }

    /// <summary>
    /// A criterion without a check's values - any other criterion, and every one recorded before checks had them - is not
    /// drawn on a card, as it was not before: the cards look as they did.
    /// </summary>
    [Fact]
    public void A_criterion_without_a_check_s_values_draws_as_before()
    {
        var tests = new CriterionResult("tests", "dotnet test", true, CriterionOutcome.Passed, 0, null);
        var legacy = Ev(EventKind.CriterionEvaluated, tests.Describe(), WorkEventPayload.CriterionPayload(tests));
        var without = Fold(TwoSteps);
        var with = Fold([.. TwoSteps, legacy]);

        Assert.Empty(with.RunChecks);
        Assert.All(with.Cards, c => Assert.Empty(c.Checks));
        Assert.Equal(without.Cards.Select(c => (c.Title, c.Activity, c.Tally, c.Version)),
            with.Cards.Select(c => (c.Title, c.Activity, c.Tally, c.Version)));
    }

    [Fact]
    public void A_run_reopened_shows_the_checks_it_showed_live()
    {
        WorkEvent[] events = [.. TwoSteps,
            Checked(new CheckFiring("lint", CheckBoundary.Step) { Step = 1, DetailPath = "d.txt" }, CriterionOutcome.Passed, 0),
            Checked(new CheckFiring("audit", CheckBoundary.Run), CriterionOutcome.Unknown, null, "npm audit",
                detail: WorkspaceChecks.NeedsPermission + " - " + WorkspaceChecks.NotAsked),
            Ev(EventKind.TaskCompleted, "done", WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed))];
        var live = Fold(events);
        var record = new RunRecord(Run, Guid.NewGuid(), "docs", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Completed",
            events.Select(e => new RunEventRecord(e.At, e.Kind.ToString(), e.Summary, e.StepNo(), e.PayloadJson)).ToArray(), [], []);

        var reopened = RunFeed.Replay(record);

        Assert.Equal(CheckLines.Of(live.Cards[0].Checks), CheckLines.Of(reopened.Cards[0].Checks));
        Assert.Equal(CheckLines.Of(live.RunChecks), CheckLines.Of(reopened.RunChecks));
        Assert.Equal("not run: needs permission", Assert.Single(CheckLines.Of(reopened.RunChecks)).Reason);
    }
}
