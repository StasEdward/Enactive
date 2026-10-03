namespace Enactive.Agents;

using Enactive.Core.Context;
using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.Execution;
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
            step.Criteria, step.SubmitTool, models.ReviewOn))
            await publish(ev);
        if (!models.ReviewOn || !result.Succeeded) return null;
        return await ReviewAttemptAsync(title, step.Messages, step.Journal, step.EvidenceStart,
            step.StepStart, step.Store, scope, models, stepNumber, changes, before, request,
            publish, ct, planSteps, stepNumber is { } number && session.Obligations?.AtStep(number) is { } at
                ? at with { ScopeNote = session.ScopeNotes.GetValueOrDefault(number) } : null,
            step.OutputSlot.Values is { } handed ? CitedPlaces.TextOf(handed) : null, step.Criteria, step.OutputSlot.Values,
            session.Builds, step.Reads, context.Restrictions, result.KeptBack);
    }
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
            if ((assessed.BudgetExhausted ?? assessed.Review.IncompleteReason) is { } unavailable)
            {
                // A review ran at all only because the worker finished (ExecuteAttemptAsync returns
                // null otherwise), so when the verdict is what is missing, the work is DONE and only
                // unconfirmed. That is DoneUnverified, and its dependents run. When instead the
                // reviewer reached a verdict that ends the step - a prohibition the work violated,
                // which also carries an IncompleteReason - it stays Incomplete, as it always was:
                // that work must not be built on.
                var verdictMissing = assessed.BudgetExhausted is not null || assessed.Review.VerdictUnavailable;
                // Why, as a code: a reviewer that said it could not tell is not a review that failed to
                // be processed, and neither is a verdict against the work (a prohibition it broke).
                result.Set(verdictMissing ? StepOutcomeKind.DoneUnverified : StepOutcomeKind.Incomplete, unavailable,
                    !verdictMissing ? OutcomeCause.ReviewRejected
                    : assessed.BudgetExhausted is null && assessed.Review.Undecided ? OutcomeCause.ReviewUndecided
                    : OutcomeCause.ReviewUnprocessable);
                await publish(scope.Ev(EventKind.ErrorObserved, prefix + unavailable, stepNumber));
                return;
            }
            if (assessed.Review.Pass) return;
            if (attempt < attempts)
            {
                RetryAfterReview(step.Messages, assessed.Review.RepairAdvice ?? assessed.Review.Notes, stepNumber is null ? "the work" : "this step");
                continue;
            }
            result.Set(StepOutcomeKind.ReviewRejected, "review not passed: " + assessed.Review.Notes);
            result.Keep = assessed.Review.Keep;
        }
    }

    private async Task RevertRejectedAsync(ToolLoopResult result, IArtifactScope store,
        RunScope scope, Func<string, ValueTask> publish, CancellationToken ct)
    {
        if (result.Kind != StepOutcomeKind.ReviewRejected || !_revertRejectedSteps) return;
        // Rejected, and still not put back: the files the review found right. The step stays rejected - nothing is built
        // on it - but what it made right is left for the person to see, rather than thrown away with the report or the
        // other file it came with; the rest of what it changed goes back. See ReviewResult.Keep.
        var keep = result.Keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = store.TouchedPaths.Where(p => keep.Contains(ShellLookup.Normal(p))).ToArray();
        if (kept.Length > 0)
            await publish("Rejected, but NOT put back: " + string.Join(", ", kept)
                + " - the review found it right and rejected the step for something else; it is left as it is, "
                + "and nothing is built on it.");
        var report = await RevertAsync(store, scope.Artifacts, ct, except: kept);
        foreach (var line in DescribeRevert(report)) await publish(line);
    }

}
