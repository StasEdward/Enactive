namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// What a run's steps, its checks and the engine's own checks come to - asked of the one module
/// that decides it, with no run around it.
///
/// <para>Until 2026-10-07 the rule was written twice, once for a quick action and once for a plan,
/// and kept in step by a comment ("the same rule as a planned run's"). The copies had already
/// drifted: a quick action that did not finish never named the checks that failed, and a blocked
/// plan offered to be resumed even where the engine kept no checkpoint to resume it from. Each of
/// these rules could be reached only by scripting a whole run in the right order.</para>
/// </summary>
public sealed class RunOutcomeDecisionTests
{
    private static SettledStep Step(int? number, string title, StepOutcomeKind? outcome, string? reason = null,
        string? blockedBecause = null, IReadOnlyList<string>? parts = null)
        => new(number, title, outcome, reason, parts, blockedBecause);

    private static RunWork Work(bool resumable, params SettledStep[] steps) => new(steps, resumable);

    private static CriterionResult Check(string name, CriterionOutcome outcome, bool required = true,
        CriterionOrigin origin = CriterionOrigin.Declared, string? detail = null, bool alreadyPassing = false)
        => new(name, "make " + name, required, outcome, outcome == CriterionOutcome.Passed ? 0 : 1, detail, origin,
            alreadyPassing);

    private static (RunOutcomeKind Outcome, string? Reason) Decide(RunWork work, SuccessReport? report = null,
        string? incompleteReason = null, params CriterionResult[] finalChecks)
    {
        var before = RunOutcomeDecision.BeforeChecks(work);
        var verification = before.Verify ? new RunVerification(report ?? SuccessReport.NothingToCheck, incompleteReason) : null;
        return RunOutcomeDecision.Settle(work, before, verification, finalChecks);
    }

    // ── one rule, whichever way the run was shaped ──────────────────────────

    /// <summary>
    /// The guarantee this module exists for. A quick action is a run of one unnumbered step that
    /// cannot be resumed; given the same step and the same checks, it must come to what a plan of
    /// that one step comes to.
    /// </summary>
    [Theory]
    [InlineData(StepOutcomeKind.Succeeded)]
    [InlineData(StepOutcomeKind.Failed)]
    [InlineData(StepOutcomeKind.ReviewRejected)]
    [InlineData(StepOutcomeKind.Incomplete)]
    [InlineData(StepOutcomeKind.DoneUnverified)]
    public void A_quick_action_and_a_plan_of_the_same_step_come_to_the_same_outcome(StepOutcomeKind kind)
    {
        var failingBuild = Check("module builds", CriterionOutcome.Failed, detail: "error CS1002 in Mail.cs");

        var quick = Decide(Work(resumable: false, Step(null, "Add the mail module", kind, "the worker stopped")),
            new SuccessReport([failingBuild]));
        var plan = Decide(Work(resumable: true, Step(1, "Add the mail module", kind, "the worker stopped")),
            new SuccessReport([failingBuild]));

        Assert.Equal(plan.Outcome, quick.Outcome);
        // The same words, but for the step number a plan has and a quick action does not.
        Assert.Equal(plan.Reason?.Replace("[1] ", ""), quick.Reason);
    }

    [Theory]
    [InlineData(new[] { StepOutcomeKind.Succeeded, StepOutcomeKind.Succeeded }, RunOutcomeKind.Completed)]
    [InlineData(new[] { StepOutcomeKind.Succeeded, StepOutcomeKind.Failed }, RunOutcomeKind.Failed)]
    [InlineData(new[] { StepOutcomeKind.ReviewRejected, StepOutcomeKind.Blocked }, RunOutcomeKind.Failed)]
    [InlineData(new[] { StepOutcomeKind.Blocked, StepOutcomeKind.Incomplete }, RunOutcomeKind.Blocked)]
    [InlineData(new[] { StepOutcomeKind.Succeeded, StepOutcomeKind.Skipped }, RunOutcomeKind.Incomplete)]
    [InlineData(new[] { StepOutcomeKind.Succeeded, StepOutcomeKind.DoneUnverified }, RunOutcomeKind.Incomplete)]
    public void Anything_that_went_wrong_outranks_anything_that_went_right(StepOutcomeKind[] steps, RunOutcomeKind expected)
    {
        var work = Work(true, steps.Select((s, i) => Step(i + 1, $"Step {i + 1}", s)).ToArray());

        Assert.Equal(expected, RunOutcomeDecision.BeforeChecks(work).Outcome);
    }

    [Fact]
    public void A_run_with_no_settled_step_is_not_complete()
        => Assert.Equal(RunOutcomeKind.Incomplete,
            RunOutcomeDecision.BeforeChecks(Work(true, Step(1, "Partition the disk", null))).Outcome);

    [Fact]
    public void A_plan_with_unresolvable_dependencies_is_not_complete_however_its_steps_went()
    {
        var work = new RunWork([Step(1, "Partition the disk", StepOutcomeKind.Succeeded)], Resumable: true, Cycle: true);

        Assert.Equal(RunOutcomeKind.Incomplete, RunOutcomeDecision.BeforeChecks(work).Outcome);
    }

    // ── when the checks are asked ───────────────────────────────────────────

    [Theory]
    [InlineData(StepOutcomeKind.Succeeded, true)]
    [InlineData(StepOutcomeKind.Incomplete, true)]
    [InlineData(StepOutcomeKind.DoneUnverified, true)]
    [InlineData(StepOutcomeKind.Failed, false)]
    [InlineData(StepOutcomeKind.Blocked, false)]
    public void Checks_are_asked_of_a_run_that_is_done_or_could_not_say_so(StepOutcomeKind kind, bool asked)
        => Assert.Equal(asked, RunOutcomeDecision.BeforeChecks(Work(true, Step(1, "Write the disk report", kind))).Verify);

    /// <summary>
    /// A skipped step is a known absence of work, not a missing proof of it: no check can make the
    /// steps that never started have happened (measured 2026-09-21).
    /// </summary>
    [Fact]
    public void Checks_are_not_asked_to_answer_for_a_step_that_never_ran()
    {
        var work = Work(true, Step(1, "Draft the email", StepOutcomeKind.Incomplete), Step(2, "Send the email", StepOutcomeKind.Skipped));

        Assert.False(RunOutcomeDecision.BeforeChecks(work).Verify);
    }

    // ── what the checks may change ──────────────────────────────────────────

    [Fact]
    public void A_check_that_proves_the_work_overrules_a_step_that_could_not_say_it_finished()
    {
        var (outcome, reason) = Decide(Work(true, Step(1, "Add the mail module", StepOutcomeKind.Incomplete, "two shell calls left open")),
            new SuccessReport([Check("module tests", CriterionOutcome.Passed)]));

        Assert.Equal(RunOutcomeKind.Completed, outcome);
        Assert.Contains("module tests", reason);
        // The overruled reason is kept, not replaced: it is real information.
        Assert.Contains("two shell calls left open", reason);
    }

    [Fact]
    public void Checks_may_not_promote_a_run_past_a_step_nobody_confirmed()
    {
        var (outcome, _) = Decide(Work(true, Step(1, "Add the mail module", StepOutcomeKind.DoneUnverified, "the review could not be used")),
            new SuccessReport([Check("module tests", CriterionOutcome.Passed)]));

        Assert.Equal(RunOutcomeKind.Incomplete, outcome);
    }

    [Fact]
    public void A_required_check_that_failed_fails_a_run_its_steps_called_done_and_is_named()
    {
        var (outcome, reason) = Decide(Work(true, Step(1, "Add the mail module", StepOutcomeKind.Succeeded)),
            new SuccessReport([Check("module tests", CriterionOutcome.Failed, detail: "2 tests failed")]));

        Assert.Equal(RunOutcomeKind.Failed, outcome);
        Assert.StartsWith("Not complete - ", reason);
        Assert.Contains("check 'module tests' failed: 2 tests failed", reason);
    }

    [Fact]
    public void A_contract_the_checks_could_not_settle_leaves_the_run_not_complete()
    {
        var (outcome, reason) = Decide(Work(true, Step(1, "Add the mail module", StepOutcomeKind.Succeeded)),
            incompleteReason: "the check 'module tests' could not be agreed");

        Assert.Equal(RunOutcomeKind.Incomplete, outcome);
        Assert.Contains("the check 'module tests' could not be agreed", reason);
    }

    // ── what is not complete, named ─────────────────────────────────────────

    /// <summary>
    /// The drift this module ended: a quick action that did not finish said only its step's reason,
    /// and a check the engine ran on the workspace and saw fail was nowhere in the line a person
    /// reads.
    /// </summary>
    [Fact]
    public void A_quick_action_that_did_not_finish_names_its_step_and_the_checks_that_failed()
    {
        var (outcome, reason) = Decide(Work(false, Step(null, "Add the mail module", StepOutcomeKind.Incomplete, "ran out of turns")),
            finalChecks: Check("No new build errors", CriterionOutcome.Failed, required: false, origin: CriterionOrigin.System,
                detail: "CS0103 in Mail.cs"));

        Assert.Equal(RunOutcomeKind.Incomplete, outcome);
        Assert.Equal("Not complete - Add the mail module - incomplete: ran out of turns; "
                     + "check 'No new build errors' failed: CS0103 in Mail.cs", reason);
    }

    [Fact]
    public void Every_step_not_confirmed_is_listed_in_plan_order_with_what_it_answers_for()
    {
        var (_, reason) = Decide(Work(true,
            Step(2, "Send the email", null),
            Step(1, "Draft the email", StepOutcomeKind.Failed, "the SMTP server refused the login", parts: ["R2"]),
            Step(3, "Archive the thread", StepOutcomeKind.Succeeded)));

        Assert.Equal("Not complete - [1] Draft the email (R2) - failed: the SMTP server refused the login; "
                     + "[2] Send the email - not run", reason);
    }

    [Fact]
    public void A_run_done_item_by_item_leads_with_what_its_items_came_to()
    {
        var work = new RunWork([Step(1, "Check disk C:", StepOutcomeKind.Succeeded), Step(2, "Check disk D:", StepOutcomeKind.Failed, "unreadable")],
            Resumable: true, ItemsCameTo: "2 item step(s): 1 done, 1 failed", Limit: "the run's time ran out");

        var (_, reason) = Decide(work);

        Assert.Equal("Not complete - 2 item step(s): 1 done, 1 failed; [2] Check disk D: - failed: unreadable (the run's time ran out)", reason);
    }

    // ── blocked ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_blocked_run_that_can_be_resumed_names_its_own_causes_and_says_to_resume()
    {
        var (outcome, reason) = Decide(Work(true,
            Step(1, "Read the mailbox", StepOutcomeKind.Blocked, blockedBecause: "needs a permission it was refused: read_mail"),
            // Waiting behind step 1 is not a cause of its own.
            Step(2, "Summarise the mailbox", StepOutcomeKind.Blocked)));

        Assert.Equal(RunOutcomeKind.Blocked, outcome);
        Assert.Equal("Blocked - [1] Read the mailbox: needs a permission it was refused: read_mail. Put that right and resume "
                     + "this run: it carries on from the blocked step(s) (2 step(s) blocked).", reason);
    }

    /// <summary>
    /// A quick action keeps no checkpoint, and neither does a plan run where the engine was given no
    /// store for one. Telling either to "resume this run" sends somebody looking for a button that
    /// cannot work.
    /// </summary>
    [Fact]
    public void A_blocked_run_that_cannot_be_resumed_says_to_run_it_again()
    {
        var (_, reason) = Decide(Work(false,
            Step(null, "Read the mailbox", StepOutcomeKind.Blocked, blockedBecause: "needs a permission it was refused: read_mail")));

        Assert.Equal("Blocked - Read the mailbox: needs a permission it was refused: read_mail. Put that right and run it again.", reason);
    }

    // ── what the engine saw for itself ──────────────────────────────────────

    /// <summary>
    /// The engine's own checks on the workspace do not decide a run: a new build error at the end does
    /// not prove this run made it, because the workspace can change around a run (commit 547efc8, run
    /// 3fe4f8). But a run called done while one of them failed must say so where a person reads the
    /// outcome, not only in a check event further up the log.
    /// </summary>
    [Fact]
    public void A_completed_run_stays_completed_and_says_which_of_the_engines_own_checks_failed()
    {
        var (outcome, reason) = Decide(Work(true, Step(1, "Add the mail module", StepOutcomeKind.Succeeded)),
            finalChecks: Check("No new build errors", CriterionOutcome.Failed, required: false, origin: CriterionOrigin.System,
                detail: "CS0103 in Mail.cs"));

        Assert.Equal(RunOutcomeKind.Completed, outcome);
        Assert.Equal("Completed, but check 'No new build errors' failed: CS0103 in Mail.cs", reason);
    }

    [Fact]
    public void A_run_the_checks_promoted_keeps_why_and_adds_what_the_engine_saw_fail()
    {
        var (outcome, reason) = Decide(Work(true, Step(1, "Add the mail module", StepOutcomeKind.Incomplete, "two shell calls left open")),
            new SuccessReport([Check("module tests", CriterionOutcome.Passed)]),
            finalChecks: Check("Produced file", CriterionOutcome.Failed, required: false, origin: CriterionOrigin.System,
                detail: "not in the workspace now"));

        Assert.Equal(RunOutcomeKind.Completed, outcome);
        Assert.StartsWith("the check(s) that decide this run passed: module tests", reason);
        Assert.EndsWith(" - but check 'Produced file' failed: not in the workspace now", reason);
    }

    [Fact]
    public void A_completed_run_whose_own_checks_all_passed_has_nothing_to_explain()
    {
        var (outcome, reason) = Decide(Work(true, Step(1, "Add the mail module", StepOutcomeKind.Succeeded)),
            finalChecks: Check("Produced file", CriterionOutcome.Passed, required: false, origin: CriterionOrigin.System));

        Assert.Equal(RunOutcomeKind.Completed, outcome);
        Assert.Null(reason);
    }
}
