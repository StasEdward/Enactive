namespace Enactive.Agents;

using Enactive.Core.Events;
using Enactive.Core.Templates;

/// <summary>One step as the run's outcome reads it.</summary>
/// <param name="Number">The step's number in the plan; null for a quick action's one step, which has none.</param>
/// <param name="Outcome">How the step settled; null for a step that never ran.</param>
/// <param name="Reason">What the step said, or the engine's record of it - a step joining items has only the record.</param>
/// <param name="Parts">The parts of the request the step answers for.</param>
/// <param name="BlockedBecause">The step's own block; null for a step only waiting behind another one.</param>
internal sealed record SettledStep(
    int? Number,
    string Title,
    StepOutcomeKind? Outcome,
    string? Reason = null,
    IReadOnlyList<string>? Parts = null,
    string? BlockedBecause = null);

/// <summary>What a run's work came to, gathered for <see cref="RunOutcomeDecision"/>.</summary>
/// <param name="Resumable">Whether the engine kept a checkpoint this run can carry on from.</param>
/// <param name="Cycle">The plan had dependencies that could never be satisfied.</param>
/// <param name="Limit">A ceiling the run hit, if any.</param>
/// <param name="ItemsCameTo">For a run done item by item, what its items came to - it leads the reason.</param>
internal sealed record RunWork(
    IReadOnlyList<SettledStep> Steps,
    bool Resumable,
    bool Cycle = false,
    string? Limit = null,
    string? ItemsCameTo = null);

/// <summary>What the run's success criteria said, after any repair they earned.</summary>
/// <param name="IncompleteReason">Why the criteria themselves could not be settled, if they could not.</param>
internal sealed record RunVerification(SuccessReport Report, string? IncompleteReason);

/// <summary>The outcome the steps alone come to, and whether the criteria are to be asked of it.</summary>
internal sealed record RunOutcomeBeforeChecks(RunOutcomeKind Outcome, string? Reason, bool Verify);

/// <summary>
/// What a run comes to: its steps' outcomes, then its success criteria, then what the engine saw
/// for itself in the workspace.
///
/// <para><b>Why one module.</b> The rule was written twice, once for a quick action and once for a
/// plan, and kept in step by a comment. The copies drifted: a quick action that did not finish never
/// named the checks that failed, and a blocked plan was told to "resume this run" even where the
/// engine kept no checkpoint to resume it from. A quick action is now what it is - a run of one
/// unnumbered step that cannot be resumed - and both ask the same two questions here.</para>
///
/// <para><b>Why two phases.</b> Between them the orchestrator runs the criteria, which runs commands
/// and may give the agent one repair. That is the only part with effects, and it stays outside, so
/// every rule about what the run comes to can be asked without a run.</para>
/// </summary>
internal static class RunOutcomeDecision
{
    /// <summary>What the steps alone come to, and whether the criteria may still change it.</summary>
    public static RunOutcomeBeforeChecks BeforeChecks(RunWork work)
    {
        var settled = Settled(work);
        var outcome = OutcomeOf(settled);
        if (work.Cycle && outcome == RunOutcomeKind.Completed)
            outcome = RunOutcomeKind.Incomplete;

        var reason = outcome == RunOutcomeKind.Blocked
            ? Blocked(work, settled)
            : RunOutcomeWords.Explain(settled, Reasons(work), work.Cycle, work.Limit);

        // Failed and Cancelled are not asked: their outcome was decided by something that actually
        // went wrong, or by the person, and a green build on top would bury it. Incomplete is asked,
        // because it means "we could not establish that it finished" - a question an exit code can
        // answer. Except when a step was SKIPPED: that is a known absence of work, and no check can
        // make the steps that never started have happened (measured 2026-09-21: a third of the work
        // called done because a file the first step had begun existed).
        var verify = outcome == RunOutcomeKind.Completed
                     || (outcome == RunOutcomeKind.Incomplete && !settled.Contains(StepOutcomeKind.Skipped));

        return new RunOutcomeBeforeChecks(outcome, reason, verify);
    }

    /// <summary>
    /// The run's outcome and the reason a person reads, once the criteria have had their say and the
    /// engine has checked the workspace as the run leaves it.
    /// </summary>
    /// <param name="verification">What the criteria said; null when <see cref="RunOutcomeBeforeChecks.Verify"/> was false.</param>
    /// <param name="finalChecks">The engine's own checks on the workspace - produced files, new build errors.</param>
    public static (RunOutcomeKind Outcome, string? Reason) Settle(RunWork work, RunOutcomeBeforeChecks before,
        RunVerification? verification, IReadOnlyList<CriterionResult> finalChecks)
    {
        var outcome = before.Outcome;
        var reason = before.Reason;
        var settled = Settled(work);

        if (verification is not null)
        {
            var adjusted = verification.IncompleteReason is not null
                           && outcome is not (RunOutcomeKind.Failed or RunOutcomeKind.Cancelled)
                ? RunOutcomeKind.Incomplete
                : verification.Report.Apply(outcome);
            // Checks may not promote a run past a MISSING verdict. They still ran, are still in the
            // report, and can still fail the run - but a step whose work was never confirmed keeps it
            // short of Completed. Without this, the promotion measured on 2026-09-21 - a run called
            // done on "the file exists" - would come back by another road: a DoneUnverified step
            // releases its dependents, so nothing is skipped and the guard in BeforeChecks never fires.
            if (adjusted == RunOutcomeKind.Completed && settled.Contains(StepOutcomeKind.DoneUnverified))
                adjusted = outcome;
            if (adjusted != outcome)
            {
                reason = adjusted == RunOutcomeKind.Completed
                    ? verification.Report.Overruling(reason)
                    : verification.IncompleteReason ?? verification.Report.Explain();
                outcome = adjusted;
            }
        }

        // What is not complete, named - every step not confirmed, with the parts of the request it
        // answers for, and every check that failed - instead of the first reason any step happened
        // to give (the user's model: "no - list concretely what is not finished").
        if (outcome is RunOutcomeKind.Failed or RunOutcomeKind.Incomplete
            && NotComplete(work, verification, finalChecks) is { Length: > 0 } open)
            reason = "Not complete - " + string.Join("; ", work.ItemsCameTo is null ? open : [work.ItemsCameTo, .. open])
                     + (work.Limit is null ? "" : $" ({work.Limit})");

        // The engine's own checks never decide a run: a new build error at the end does not prove
        // THIS run made it, because the workspace can change around a run (run 3fe4f8, commit
        // 547efc8), and a file being where the run left it says nothing about whether it is right.
        // But a run called done while one of them failed says so in the line a person reads - the
        // Inbox, the status line - and not only in a check event further up the log.
        if (outcome == RunOutcomeKind.Completed
            && finalChecks.Where(c => c.Outcome == CriterionOutcome.Failed).Select(CheckLine).ToArray() is { Length: > 0 } failed)
        {
            var but = "but " + string.Join("; ", failed);
            reason = string.IsNullOrWhiteSpace(reason) ? "Completed, " + but : $"{reason} - {but}";
        }

        return (outcome, reason);
    }

    /// <summary>
    /// The run's outcome from its steps'. Anything that went wrong outranks anything that went right:
    /// a plan is not finished because most of it finished.
    /// </summary>
    private static RunOutcomeKind OutcomeOf(IReadOnlyCollection<StepOutcomeKind> steps)
    {
        if (steps.Count == 0)
            return RunOutcomeKind.Incomplete;

        if (steps.Any(s => s is StepOutcomeKind.Failed or StepOutcomeKind.ReviewRejected))
            return RunOutcomeKind.Failed;

        // After a failure, before everything short of it: nothing is known to be wrong, and the run is
        // not over - it waits for its cause to be put right (Phase 7).
        if (steps.Contains(StepOutcomeKind.Blocked))
            return RunOutcomeKind.Blocked;

        // DoneUnverified with them: its work was done, but a run is Completed only on verdicts that were
        // actually given. Left out of this line it would fall through to Completed.
        if (steps.Any(s => s is StepOutcomeKind.Incomplete or StepOutcomeKind.Skipped or StepOutcomeKind.DoneUnverified))
            return RunOutcomeKind.Incomplete;

        return RunOutcomeKind.Completed;
    }

    /// <summary>
    /// A blocked run leads with what blocks it - each step blocked for a cause of its own, not the ones
    /// only waiting behind them - and with what to do about it. Only a run with a checkpoint is told to
    /// resume; anything else is told to run again, because a resume it cannot do sends somebody looking
    /// for a button that does not work.
    /// </summary>
    private static string Blocked(RunWork work, IReadOnlyList<StepOutcomeKind> settled)
    {
        var causes = Ordered(work)
            .Where(s => s.Outcome == StepOutcomeKind.Blocked && !string.IsNullOrWhiteSpace(s.BlockedBecause))
            .Select(s => $"{Label(s)}: {s.BlockedBecause}")
            .ToArray();
        var what = causes.Length == 0 ? "Blocked" : "Blocked - " + string.Join("; ", causes);
        if (!work.Resumable)
            return what + ". Put that right and run it again.";

        var tally = RunOutcomeWords.Explain(settled, [], work.Cycle, work.Limit);
        return what + ". Put that right and resume this run: it carries on from the blocked step(s)"
               + (tally is { } t ? $" ({t})." : ".");
    }

    /// <summary>Each thing that keeps a run from Completed, as a line: a step not confirmed, a check that failed.</summary>
    private static string[] NotComplete(RunWork work, RunVerification? verification, IReadOnlyList<CriterionResult> finalChecks)
    {
        var lines = new List<string>();
        foreach (var step in Ordered(work))
        {
            if (step.Outcome == StepOutcomeKind.Succeeded) continue;
            var parts = step.Parts is { Count: > 0 } ids ? $" ({string.Join(", ", ids)})" : "";
            var said = step.Outcome is { } outcome ? Word(outcome) : "not run";
            var why = !string.IsNullOrWhiteSpace(step.Reason) ? ": " + Clip(step.Reason, 300) : "";
            lines.Add($"{Label(step)}{parts} - {said}{why}");
        }
        if (verification?.IncompleteReason is { } unverified) lines.Add(Clip(unverified, 300));
        foreach (var check in (verification?.Report.Blocking ?? []).Concat(finalChecks.Where(c => c.Outcome == CriterionOutcome.Failed)))
            lines.Add(CheckLine(check));
        return lines.Distinct().ToArray();
    }

    private static string CheckLine(CriterionResult check)
        => $"check '{check.Name}' {check.Outcome.ToString().ToLowerInvariant()}"
           + (string.IsNullOrWhiteSpace(check.Detail) ? "" : ": " + Clip(check.Detail, 200));

    private static StepOutcomeKind[] Settled(RunWork work)
        => work.Steps.Where(s => s.Outcome is not null).Select(s => s.Outcome!.Value).ToArray();

    /// <summary>
    /// What the steps that did not succeed said, in plan order - so the cause that leads is the same
    /// however parallel steps happened to finish. A skipped step's cause is another step's failure.
    /// </summary>
    private static IEnumerable<string?> Reasons(RunWork work)
    {
        var said = Ordered(work)
            .Where(s => s.Outcome is { } o && o is not (StepOutcomeKind.Succeeded or StepOutcomeKind.Skipped))
            .Select(s => s.Reason);
        return work.ItemsCameTo is null ? said : [work.ItemsCameTo, .. said];
    }

    private static IEnumerable<SettledStep> Ordered(RunWork work)
        => work.Steps.OrderBy(s => s.Number ?? int.MaxValue);

    /// <summary>A plan's step by its number and title; a quick action's one step by its title alone.</summary>
    private static string Label(SettledStep step) => step.Number is { } n ? $"[{n}] {step.Title}" : step.Title;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>A step's outcome as one word in a line about the run.</summary>
    internal static string Word(StepOutcomeKind kind) => kind switch
    {
        StepOutcomeKind.Succeeded => "done",
        StepOutcomeKind.ReviewRejected => "review rejected",
        StepOutcomeKind.Incomplete => "incomplete",
        StepOutcomeKind.Skipped => "skipped",
        StepOutcomeKind.DoneUnverified => "done, not verified",
        StepOutcomeKind.Blocked => "blocked",
        _ => "failed"
    };
}
