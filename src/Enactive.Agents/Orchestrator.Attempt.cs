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
            step.Criteria, step.SubmitTool, models.ReviewOn, step.LoadedTools, session.ChangeLimits, title))
            await publish(ev);
        // The step's own word that it cannot go on, with nothing the engine found behind it, is reviewed as a finished step
        // is (SaidBlockedOnly): whether its own part is done is the review's to say, not the report's.
        if (!models.ReviewOn || !(result.Succeeded || SaidBlockedOnly(result))) return null;
        return await ReviewAttemptAsync(title, step.Messages, step.Journal, step.EvidenceStart,
            step.StepStart, step.Store, scope, models, stepNumber, changes, before, request,
            publish, ct, planSteps, stepNumber is { } number && session.Obligations?.AtStep(number) is { } at
                ? at with { ScopeNote = session.ScopeNotes.GetValueOrDefault(number) } : null,
            step.OutputSlot.Values is { } handed ? CitedPlaces.TextOf(handed) : null, step.Criteria, step.OutputSlot.Values,
            session.Builds, step.Reads, context.Restrictions, result.KeptBack,
            saidBlocked: SaidBlockedOnly(result) ? result.Reason : null);
    }

    /// <summary>
    /// The step ended on its own report that it cannot go on, and on nothing the engine measured - no refused permission,
    /// no missing input, no tool it was kept from (StepEnding.ReportedBlocked). Such a report used to end the step without
    /// a review: on 2026-10-08 a read-only step wrote its whole analysis, called report_blocked because "the next step
    /// requires writing tests", and the run ended BLOCKED with the analysis done - seven minutes and the run lost.
    /// </summary>
    private static bool SaidBlockedOnly(ToolLoopResult result)
        => result.Kind == StepOutcomeKind.Blocked && result.Cause == OutcomeCause.BlockedReported;

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
        var attempts = models.ReviewOn ? _reviewRetries + 1 : 1;
        var usedFallback = false;
        for (var attempt = 1; attempt <= attempts; attempt++)
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
            // A step that said it cannot go on: the review found its own part done, and it is - the report stays in the
            // journal as its word, and the steps after it go on; anything else, and it ends blocked as it reported. Not
            // tried again: the step has said it cannot, and a retry would ask it to.
            if (SaidBlockedOnly(result))
            {
                if (verdict is ReviewVerdict.Pass)
                {
                    await publish(scope.Ev(EventKind.ContextAssembled, prefix + "The step said it cannot go on, and the review found "
                        + "its own part done: it stands as done, its report as a note - " + result.Reason, stepNumber));
                    result.Set(StepOutcomeKind.Succeeded, null);
                }
                return;
            }
            // No verdict to act on - the reviewer could not tell, no answer could be used, the budget was spent: the work
            // is done and only unconfirmed (ReviewVerdict.Missing says what that makes of the step, and why).
            if (verdict.Missing is { } missing)
            {
                result.Set(missing.Kind, verdict.Notes, missing.Cause);
                await publish(scope.Ev(EventKind.ErrorObserved, prefix + verdict.Notes, stepNumber));
                return;
            }
            if (verdict is not ReviewVerdict.Fail fail) return;   // a pass: the step stands
            if (attempt < attempts)
            {
                RetryAfterReview(step.Messages, fail.RepairAdvice, stepNumber is null ? "the work" : "this step");
                continue;
            }
            result.Set(StepOutcomeKind.ReviewRejected, "review not passed: " + fail.Notes);
            result.Keep = fail.Keep;
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
