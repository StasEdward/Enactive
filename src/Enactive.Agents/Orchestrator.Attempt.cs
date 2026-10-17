namespace Enactive.Agents;

using Enactive.Core.Context;
using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;

public sealed partial class Orchestrator
{
    /// <summary>One worker execution followed by review. Used by the shared retry/fallback runner below.</summary>
    private async Task<AttemptReview?> ExecuteAttemptAsync(
        RunSession session, StepAttemptState step, RunModels models,
        IChatProvider provider, ModelRef model, WorkContext context,
        ToolLoopResult result, string title, string request, int? stepNumber,
        IWorkspaceChanges? changes, WorkspaceSnapshot? before,
        Func<WorkEvent, ValueTask> publish, CancellationToken ct, IReadOnlyList<string>? planSteps = null,
        ToolCallOrigin attemptOrigin = ToolCallOrigin.Native)
    {
        var scope = session.Scope;
        await foreach (var ev in RunToolLoopAsync(scope.TaskId, scope.RunId, provider, model.Model,
            models.Worker, step.Messages, scope.Artifacts, context, step.Store, step.Journal, step.Reads,
            stepNumber, result, scope.Budget, scope.Granted, ct, model.ProviderId,
            step.RestartFrom, changes, before, attemptOrigin, step.Output, step.OutputSlot, step.Boundary, step.WithholdUnchecked,
            step.Criteria, step.SubmitTool, models.ReviewOn, step.LoadedTools, session.ChangeLimits, title, checks: session.Checks))
            await publish(ev);

        // The lists this step consumes, counted by the engine before any review - and with review off, before the step is
        // taken as done (Plan Phase 1.3). Short, it goes back to the step on the gate's own budget and the reviewer is not
        // asked: a reviewer shown two good notes passes them, and the ten pages with nothing behind them are a count the
        // engine can make itself. Only a step that ended as done is counted; any other end is already not done - except a
        // step that only SAID it cannot go on, which a review is about to judge as a finished step (SaidBlockedOnly): its
        // pass made the step Succeeded with the gate never run, one page of two standing as done. With review off its word
        // stands as Blocked, and there is nothing to count.
        var gated = result.Succeeded || (models.ReviewOn && SaidBlockedOnly(result));
        var coverage = gated && stepNumber is { } gatedNo && step.Coverage.Count > 0
            ? StepCoverageGate.Check(step.Coverage, session.Outputs.Values, step.OutputSlot.Build(gatedNo, title, step.Output), gatedNo)
            : null;
        if (coverage is not null && stepNumber is { } checkedNo)
        {
            if (!coverage.Passed)
            {
                // The reviewer's emission (ReviewAttemptAsync) is bypassed, so the gate says its own verdict - as the
                // engine's, so nobody reads a reviewer's judgement into a count.
                await publish(scope.Event(EventKind.ReviewFailed,
                    $"[{checkedNo}] FAIL (the engine's coverage check - no reviewer was asked): {coverage.Notes}",
                    WorkEventPayload.EngineCheckPayload(checkedNo)));
                return new(new ReviewResult(new ReviewVerdict.Fail(coverage.Notes, coverage.RepairAdvice, [])), ByGate: true);
            }
            await publish(scope.Ev(EventKind.ContextAssembled, $"[{checkedNo}] The engine's coverage check passed: "
                + string.Join("; ", coverage.Checks.Select(c => $"{c.Typed.SourceField} of step {c.Typed.SourceStep + 1}, {c.Line()}")), checkedNo));
        }

        // The workspace checks a person declared for what this step changed (Plan Phase 3.2), after the coverage count and
        // before any review: a blocking one that failed sends the step back on the gate's own budget with the check's
        // output, and the reviewer is not asked - an exit code is not an opinion, and a reviewer shown a failing build
        // would only be asked to agree with it. An advisory one is evidence for the review. Run on a step that ended as done
        // (or only said it could not go on, which a review is about to judge as done); any other end is already not done.
        var checks = gated ? await StepChecksAsync(session, step, stepNumber, context, changes, before, publish, ct) : null;
        if (checks?.Failed is { } failedCheck)
        {
            var advice = WorkspaceChecks.RepairAdvice(failedCheck.Check.Id, failedCheck.Result);
            var notes = $"the engine's check '{failedCheck.Check.Id}' failed"
                        + (failedCheck.Result.ExitCode is { } exit ? $" (exit {exit})" : "")
                        + (string.IsNullOrWhiteSpace(failedCheck.Result.Detail) ? "" : ": " + Gist(failedCheck.Result.Detail!, 300));
            await publish(scope.Event(EventKind.ReviewFailed,
                $"{(stepNumber is { } failedNo ? $"[{failedNo}] " : "")}FAIL (the engine's check '{failedCheck.Check.Id}' - no reviewer was asked): {notes}",
                WorkEventPayload.EngineCheckPayload(stepNumber, failedCheck.Check.Id)));
            return new(new ReviewResult(new ReviewVerdict.Fail(notes, advice, [])), ByGate: true) { ByCheck = failedCheck.Check.Id };
        }

        if (coverage is not null && stepNumber is not null)
        {
            // Covered, but some items only mentioned (StepCoverageGate.Outcome.OnlyMentioned): one call naming every path
            // covers every item, so the count says nothing was done with them. With no review there is no verdict to
            // approve them, and the step is done but not confirmed - never Succeeded on a count a single call can make.
            // With review on the verdict decides as for any step: a pass stands, a fail is a fail, and a missing verdict
            // is already DoneUnverified (ReviewVerdict.Missing).
            //
            // Items a step that filters left out, each with a reason (StepCoverageGate.Outcome.Exclusions): the engine
            // checked the partition, and only a reviewer can say the request permits each omission. With no review there is
            // no verdict to permit them, and the step is done but not confirmed - a reason alone never authorises leaving
            // an item out. With review on, the reviewer is asked (ReviewerBlock) and its verdict decides; a missing one
            // says so below (RunAttemptsAsync). The same decision as the join's (StepCoverageGate.Outcome.Unconfirmed).
            if (!models.ReviewOn && coverage.Unconfirmed(StepCoverageGate.Approval.None) is { } unconfirmed)
            {
                result.Set(StepOutcomeKind.DoneUnverified,
                    unconfirmed.Reason + (checks?.NotRun is { } alsoNotRun ? " " + alsoNotRun + "." : ""), unconfirmed.Cause);
                return null;
            }
        }

        // A blocking check that did not run - the policy would have asked, it ran out of time, it ran no tests - has verified
        // nothing, and it is what a person said this kind of change is confirmed by: with no review, the step is done and
        // not confirmed; with one, a pass makes it no more than that either (RunAttemptsAsync).
        if (!models.ReviewOn && result.Succeeded && checks?.NotRun is { } notRun)
        {
            result.Set(StepOutcomeKind.DoneUnverified, notRun, OutcomeCause.CheckNotRun);
            await publish(scope.Ev(EventKind.ErrorObserved, (stepNumber is { } unverifiedNo ? $"[{unverifiedNo}] " : "") + notRun, stepNumber));
            return null;
        }

        // The step's own word that it cannot go on, with nothing the engine found behind it, is reviewed as a finished step
        // is (SaidBlockedOnly): whether its own part is done is the review's to say, not the report's.
        if (!models.ReviewOn || !(result.Succeeded || SaidBlockedOnly(result))) return null;
        return (await ReviewAttemptAsync(title, step.Messages, step.Journal, step.EvidenceStart,
            step.StepStart, step.Store, scope, models, stepNumber, changes, before, request,
            publish, ct, planSteps, stepNumber is { } number && session.Obligations?.AtStep(number) is { } at
                ? at with { ScopeNote = session.ScopeNotes.GetValueOrDefault(number) } : null,
            step.OutputSlot.Values is { } handed ? CitedPlaces.TextOf(handed) : null, step.Criteria, step.OutputSlot.Values,
            session.Builds, step.Reads, context.Restrictions, result.KeptBack,
            saidBlocked: SaidBlockedOnly(result) ? result.Reason : null, coverage: coverage)) with
            {
                Coverage = coverage, ChecksNotRun = checks?.NotRun
            };
    }

    /// <summary>
    /// The step ended on its own report that it cannot go on, and on nothing the engine measured - no refused permission,
    /// no missing input, no tool it was kept from (StepEnding.ReportedBlocked). Such a report used to end the step without
    /// a review: on 2026-10-08 a read-only step wrote its whole analysis, called report_blocked because "the next step
    /// requires writing tests", and the run ended BLOCKED with the analysis done - seven minutes and the run lost.
    /// </summary>
    private static bool SaidBlockedOnly(ToolLoopResult result)
        => result.Kind == StepOutcomeKind.Blocked && result.Cause == OutcomeCause.BlockedReported;

    /// <summary>What a step that reported a block nothing stands behind is told when it is tried again.</summary>
    internal const string NotABlock = "Nothing stops this step: report_blocked is for a cause only a person can remove - a "
        + "refused permission, an input that is not there - and there is none. Do the step's own part, and finish it with what you found.";

    /// <summary>
    /// The run's guard on what the request says may be changed - only when it says something (a change limit in the
    /// contract): a run whose request sets none asks nothing. Put to the planning model, the one that read the request
    /// for the plan and its limits.
    /// </summary>
    private ChangeLimitGuard? ChangeLimitsFor(Intent intent, RunModels models, RunScope scope)
        => intent.Context.Restrictions.Where(r => r.Effect == Enactive.Core.Tools.ForbiddenTaskEffect.FileChange)
                .Select(r => r.SourceQuote).ToArray() is { Length: > 0 } limits
            ? new ChangeLimitGuard(intent.RawText, limits, models.PlanProvider, models.Plan, scope.Budget,
                _options.GenerationBudgets.For(Enactive.Core.Chat.GenerationPurpose.Planning))
            : null;
    /// <summary>One retry lifecycle for quick and DAG. Transcript, journal and read coverage stay
    /// together; provider fallback is one-shot and does not consume a review attempt.</summary>
    private async Task RunAttemptsAsync(
        RunSession session, StepAttemptState step, RunModels models,
        IChatProvider provider, ModelRef model, WorkContext context, ToolLoopResult result,
        string title, string request, int? stepNumber, IWorkspaceChanges? changes,
        WorkspaceSnapshot? before, Func<WorkEvent, ValueTask> publish, CancellationToken ct,
        IReadOnlyList<string>? planSteps = null, StepComplexity complexity = StepComplexity.Normal)
    {
        var scope = session.Scope;
        var prefix = stepNumber is { } number ? $"[{number}] " : "";
        // Two budgets, counted apart: the review's, and the coverage gate's (StepCoverageGate). A gate failure does not
        // use up a review attempt, nor a review failure a gate attempt - shared, a step sent back once for an item it
        // had not covered reached its review with no retry left, and the reviewer's first point ended it. Every
        // attempt fails one of them or ends the step, so the loop ends: at most reviews + gates - 1 attempts, plus the
        // one-shot fallback, and the run's own budget stops a tool loop before either is spent.
        var reviews = models.ReviewOn ? _reviewRetries + 1 : 1;
        var gates = (_options.GateRetries is { } gateRetries ? Math.Clamp(gateRetries, 0, 5) : models.ReviewOn ? _reviewRetries : 1) + 1;
        var reviewFailures = 0;
        var gateFailures = 0;
        var usedFallback = false;
        for (var attempt = 1; ; attempt++)
        {
            // Quick does not dispatch DAG steps. Retain its existing pre-attempt budget boundary.
            if (stepNumber is null && scope.Budget.Exhausted is { } spent)
            {
                result.Set(StepOutcomeKind.Incomplete, spent);
                await publish(scope.Ev(EventKind.ErrorObserved, spent));
                return;
            }

            AttemptReview? assessed;
            try
            {
                assessed = await ExecuteAttemptAsync(session, step, models, provider, model, context,
                    result, title, request, stepNumber, changes, before, publish, ct, planSteps,
                    attempt > 1 ? ToolCallOrigin.Retry : ToolCallOrigin.Native);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RetryBudgetExceededException)
            {
                if (usedFallback || _modelResolver.NextOnFailure(models.Worker.ModelPolicy, model) is not { } fallback)
                    throw;
                usedFallback = true;
                await publish(scope.Route(stepNumber is null ? "worker" : "step", fallback,
                    $"{prefix}{model.ProviderId}/{model.Model} failed ({ex.Message}) — "
                    + $"retrying on the fallback {fallback.ProviderId}/{fallback.Model}",
                    stepNumber, stepNumber is null ? null : complexity));
                model = fallback;
                provider = RunProvider(fallback.ProviderId, scope.Budget);
                attempt--;
                continue;
            }

            if (assessed is null) return;
            var verdict = assessed.Review.Verdict;
            // Short of the coverage it owes: tried again with the items and what each needs, on the gate's budget, and
            // rejected with the count once that is spent. Nothing is kept back from the revert - no reviewer found any
            // file right.
            if (assessed.ByGate && verdict is ReviewVerdict.Fail shortOf)
            {
                // One budget for the engine's gate, whichever of its checks sent the step back - the coverage count or a
                // blocking declared check (Plan Phase 3.2): both are the engine's, not a review's, and neither uses up a
                // review attempt.
                if (++gateFailures < gates)
                {
                    // A step that said it cannot go on is told, as after a review that failed it, that nothing stops it.
                    var repair = shortOf.RepairAdvice + (SaidBlockedOnly(result) ? " " + NotABlock : "");
                    if (assessed.ByCheck is not null)
                        RetryAfterDeclaredCheck(step.Messages, repair, stepNumber is null ? "the work" : "this step");
                    else
                        RetryAfterCoverageCheck(step.Messages, repair, stepNumber is null ? "the work" : "this step");
                    continue;
                }
                // ReviewRejected, as a reviewer's last rejection is - the same consequences - with a cause of its own:
                // the report says the engine's check rejected it, not a review nobody gave.
                result.Set(StepOutcomeKind.ReviewRejected, shortOf.Notes,
                    assessed.ByCheck is not null ? OutcomeCause.CheckGateRejected : OutcomeCause.CoverageGateRejected);
                result.RejectedByCheck = assessed.ByCheck;
                result.Keep = [];
                return;
            }
            // What is left cannot be reached within the request: said, and not tried again - a retry could only go beyond
            // the request (ReviewVerdict.Unreachable). The step's work stays as it is.
            if (verdict is ReviewVerdict.Unreachable unreachable)
            {
                result.Set(StepOutcomeKind.Incomplete, "not reachable within the request: " + unreachable.Notes, OutcomeCause.ReviewUnreachable);
                return;
            }
            // A step that said it cannot go on, with nothing the engine found to stop it: the review found its own part done,
            // and it is - the report stays in the journal as its word, and the steps after it go on. Failed, it is an attempt
            // like any other, tried again with what the review said and told that nothing blocks it. It used to end BLOCKED:
            // on 2026-10-09 a read-only step took "this step changes no file" for a stop, put its findings in report_blocked,
            // the review found one of them wrong - and the whole run ended Blocked in a minute and a half, with nothing for a
            // person to remove. Without a verdict, the step's word stands, as before.
            if (SaidBlockedOnly(result))
            {
                if (verdict is ReviewVerdict.Fail notDone)
                {
                    await publish(scope.Ev(EventKind.ErrorObserved, prefix + "The step said it cannot go on, but nothing stops it and "
                        + "the review found its part not done: it is an attempt like any other - " + notDone.Notes, stepNumber));
                    if (++reviewFailures < reviews)
                    {
                        RetryAfterReview(step.Messages, notDone.RepairAdvice + " " + NotABlock, stepNumber is null ? "the work" : "this step");
                        continue;
                    }
                    result.Set(StepOutcomeKind.ReviewRejected, "review not passed: " + notDone.Notes);
                    result.Keep = notDone.Keep;
                    return;
                }
                if (verdict is not ReviewVerdict.Pass)
                    return;
                await publish(scope.Ev(EventKind.ContextAssembled, prefix + "The step said it cannot go on, and the review found "
                    + "its own part done: it stands as done, its report as a note - " + result.Reason, stepNumber));
                result.Set(StepOutcomeKind.Succeeded, null);
                // On to the pass below: it stands only as far as the pass approved the step's coverage, as any pass does.
            }
            // No verdict to act on - the reviewer could not tell, no answer could be used, the budget was spent: the work
            // is done and only unconfirmed (ReviewVerdict.Missing says what that makes of the step, and why).
            // Items it left out, or only mentioned, with no verdict to approve them: the reason it is unconfirmed names them
            // too (StepCoverageGate.Outcome.Unconfirmed, with nothing approved).
            if (verdict.Missing is { } missing)
            {
                result.Set(missing.Kind, (assessed.Coverage?.Unconfirmed(StepCoverageGate.Approval.None) is { } unconfirmed
                    ? verdict.Notes + " " + unconfirmed.Reason : verdict.Notes)
                    + (assessed.ChecksNotRun is { } alsoNotRun ? " " + alsoNotRun + "." : ""), missing.Cause);
                await publish(scope.Ev(EventKind.ErrorObserved, prefix + verdict.Notes, stepNumber));
                return;
            }
            // A pass from a review that could not judge what the step left out - not shown it for room, or a criteria
            // review, which judges its criteria and nothing else - approves the work, not the omissions: done, not
            // confirmed, the exclusions named and why the review did not decide them. Its mentions it does approve
            // (StepCoverageGate.Approval says why the two differ).
            if (verdict is ReviewVerdict.Pass && assessed.NotAskedAboutExclusions is { } notAsked
                && assessed.Coverage?.Unconfirmed(StepCoverageGate.Approval.WorkOnly) is { } leftOut)
            {
                result.Set(StepOutcomeKind.DoneUnverified, $"{leftOut.Reason} ({notAsked}.)"
                    + (assessed.ChecksNotRun is { } alsoNotRun ? " " + alsoNotRun + "." : ""), leftOut.Cause);
                await publish(scope.Ev(EventKind.ErrorObserved, prefix + leftOut.Reason, stepNumber));
                return;
            }
            // A pass with a blocking check that did not run (Plan Phase 3.2): the review approved the work as it read it,
            // and the check a person said this kind of change is confirmed by verified nothing - done, never Succeeded.
            if (verdict is ReviewVerdict.Pass && assessed.ChecksNotRun is { } notRun)
            {
                result.Set(StepOutcomeKind.DoneUnverified, notRun, OutcomeCause.CheckNotRun);
                await publish(scope.Ev(EventKind.ErrorObserved, prefix + notRun, stepNumber));
                return;
            }
            if (verdict is not ReviewVerdict.Fail fail) return;   // a pass: the step stands
            if (++reviewFailures < reviews)
            {
                RetryAfterReview(step.Messages, fail.RepairAdvice, stepNumber is null ? "the work" : "this step");
                continue;
            }
            result.Set(StepOutcomeKind.ReviewRejected, "review not passed: " + fail.Notes);
            result.Keep = fail.Keep;
            return;
        }
    }

    /// <param name="publish">Publishes one line about the revert, with its payload (WorkEventPayload.RevertPayload).</param>
    private async Task RevertRejectedAsync(ToolLoopResult result, IArtifactScope store,
        RunScope scope, int? stepNo, Func<string, string, ValueTask> publish, CancellationToken ct)
    {
        if (result.Kind != StepOutcomeKind.ReviewRejected || !_options.RevertRejectedSteps) return;
        // Rejected, and still not put back: the files the review found right. The step stays rejected - nothing is built
        // on it - but what it made right is left for the person to see, rather than thrown away with the report or the
        // other file it came with; the rest of what it changed goes back. See ReviewResult.Keep.
        var keep = result.Keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = store.TouchedPaths.Where(p => keep.Contains(ShellLookup.Normal(p))).ToArray();
        if (kept.Length > 0)
            await publish("Rejected, but NOT put back: " + string.Join(", ", kept)
                + " - the review found it right and rejected the step for something else; it is left as it is, "
                + "and nothing is built on it.", WorkEventPayload.RevertPayload(stepNo, [], kept));
        var report = await RevertAsync(store, scope.Artifacts, ct, except: kept);
        foreach (var (line, reverted, left) in DescribeRevert(report))
            await publish(line, WorkEventPayload.RevertPayload(stepNo, reverted, left));
    }

}
