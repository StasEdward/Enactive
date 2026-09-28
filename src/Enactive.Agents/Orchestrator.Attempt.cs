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
        Func<WorkEvent, ValueTask> publish, CancellationToken ct, IReadOnlyList<string>? planSteps = null)
    {
        var scope = session.Scope;
        await foreach (var ev in RunToolLoopAsync(scope.TaskId, scope.RunId, provider, model.Model,
            models.Worker, step.Messages, scope.Artifacts, context, step.Store, step.Journal, step.Reads,
            stepNumber, result, scope.Budget, scope.Granted, ct, model.ProviderId,
            step.RestartFrom, changes, before))
            await publish(ev);
        if (!models.ReviewOn || !result.Succeeded) return null;
        return await ReviewAttemptAsync(title, step.Messages, step.Journal, step.EvidenceStart,
            step.StepStart, step.Store, scope, models, stepNumber, changes, before, request,
            publish, ct, planSteps, stepNumber is { } number ? session.Obligations?.AtStep(number) : null);
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
                    result, title, request, stepNumber, changes, before, publish, ct, planSteps);
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
                result.Set(StepOutcomeKind.Incomplete, unavailable);
                await publish(scope.Ev(EventKind.ErrorObserved, prefix + unavailable, stepNumber));
                return;
            }
            if (assessed.Review.Pass) { session.ObserveReview(assessed.Review); return; }
            if (attempt < attempts)
            {
                RetryAfterReview(step.Messages, assessed.Review.RepairAdvice ?? assessed.Review.Notes, stepNumber is null ? "the work" : "this step");
                continue;
            }
            // Preserve the two hosts' existing terminal wording.
            var reason = stepNumber is not null && assessed.ProofRejected
                ? "not shown to be done: " : "review not passed: ";
            result.Set(StepOutcomeKind.ReviewRejected, reason + assessed.Review.Notes);
        }
    }

    private async Task RevertRejectedAsync(StepOutcomeKind outcome, IArtifactScope store,
        RunScope scope, Func<string, ValueTask> publish, CancellationToken ct)
    {
        if (outcome != StepOutcomeKind.ReviewRejected || !_revertRejectedSteps) return;
        var report = await RevertAsync(store, scope.Artifacts, ct);
        foreach (var line in DescribeRevert(report)) await publish(line);
    }

}
