namespace Enactive.Agents;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
// The pure parsing/formatting members moved to ToolCallParsing (FIX_PLAN §9d, cut 1). Imported
// statically so every call site here reads exactly as it did before the move: a refactor cannot be
// verified differentially, so the less of it is visible at the call sites, the better.
using static Enactive.Agents.ToolCallParsing;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;

public sealed partial class Orchestrator
{
    private async Task<ReviewResult> ReconcileRunAsync(RunSession session, RunModels models, CancellationToken ct)
    {
        if (session.Scope.Budget.TurnExhausted is { } spent)
            return new(false, spent) { BudgetExhausted = spent };
        try
        {
            var files = new List<WrittenFile>();
            foreach (var store in session.Stores) files.AddRange(await ReadWrittenAsync(store, ct));
            var current = files.DistinctBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            return await InScopeAsync(session.Scope.RunId, session.Scope.TaskId, null,
                () => _stepReview.ReconcileAsync(string.Join("\n", session.Digest),
                    session.RunEvidence().Describe(maxChars: _evidenceBudget),
                    current.Select(f => f.RelativePath).ToArray(), current, session.Obligations!,
                    models.ReviewProvider!, models.ReviewModel, ct, session.Scope.Budget.TurnExhaustedAfter));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, ex.Message) { IncompleteReason = "Final review unavailable: " + ex.Message }; }
    }

    private sealed record AttemptReview(ReviewResult Review, bool ProofRejected = false, string? BudgetExhausted = null);

    /// <summary>
    /// Shared review/proof phase of an attempt. Callers own retries, rollback, checkpoints and
    /// transcript lifetime, and explicitly supply both evidence and current-step boundaries.
    /// </summary>
    private async Task<AttemptReview> ReviewAttemptAsync(
        string title, List<ChatMessage> messages, ExecutionJournal journal, int evidenceStart, int stepStart,
        IArtifactScope store, RunScope scope, RunModels models, int? stepNumber,
        IWorkspaceChanges? changes, WorkspaceSnapshot? before, string request,
        Func<WorkEvent, ValueTask> publish, CancellationToken ct, IReadOnlyList<string>? planSteps = null,
        RequestObligations? obligations = null)
    {
        var prefix = stepNumber is { } number ? $"[{number}] " : "";
        ValueTask Emit(EventKind kind, string summary) => publish(scope.Ev(kind, prefix + summary, stepNumber));
        async ValueTask Usage(int prompt, int completion, int? cached, int? created)
        {
            if (prompt + completion > 0)
                await publish(scope.Usage(WorkEventPayload.WorkPurpose.Review, models.Review!,
                    prompt, completion, stepNumber, cached, created));
        }

        if (scope.Budget.TurnExhausted is { } beforeReview)
            return new(new ReviewResult(false, beforeReview), BudgetExhausted: beforeReview);
        await Emit(EventKind.ReviewRequested, stepNumber is null ? "reviewing…" : "reviewing with reasoner…");
        var (review, mode) = await ReviewAsync(
            title, messages, journal, evidenceStart, stepStart, scope.Artifacts, store,
            models.ReviewProvider!, models.ReviewModel, ct, changes, before, request,
            obligations ?? RequestObligations.Create(request, title, stepNumber, planSteps), scope.Budget.TurnExhaustedAfter);
        await Usage(review.PromptTokens, review.CompletionTokens, review.CachedPromptTokens, review.CacheCreationPromptTokens);
        if (review.BudgetExhausted is { } reviewSpent)
            return new(review, BudgetExhausted: reviewSpent);
        if (review.IncompleteReason is not null) return new(review);
        if (!review.Pass)
        {
            await Emit(EventKind.ReviewFailed, $"FAIL ({mode} review): {review.Notes}");
            return new(review);
        }

        await Emit(EventKind.ReviewPassed,
            $"PASS ({mode} review){(string.IsNullOrEmpty(review.Notes) ? "" : ": " + review.Notes)}");
        if (review.Soundness is not { } proven) return new(review);
        if (proven.Sound)
        {
            await Emit(EventKind.ReviewPassed, "PASS (soundness): " + proven.Reason);
            return new(review);
        }

        await Emit(EventKind.ReviewFailed, "FAIL (soundness): " + proven.Reason);
        return new(new ReviewResult(false, proven.Reason), ProofRejected: true);
    }

    /// <summary>
    /// Asks the reviewer about the work just done. Shared by the QuickAction path and by a DAG step,
    /// so a configured reviewer applies to both — it used to run for plan steps only, while the
    /// planner was told to prefer QuickAction, which left most ordinary requests unreviewed.
    ///
    /// Fails CLOSED: a reviewer that cannot answer has not approved anything.
    /// </summary>
    private async Task<(ReviewResult Result, ReviewMode Mode)> ReviewAsync(
        string title, List<ChatMessage> convo, ExecutionJournal journal, int evidenceStart,
        int stepStart, List<ArtifactRef> artifacts, IArtifactScope store,
        IChatProvider reviewProvider, string reviewModel, CancellationToken ct,
        IWorkspaceChanges? changes = null, WorkspaceSnapshot? before = null,
        // The user's own request, verbatim - see Reviewer.ReviewAsync's own parameter of this name.
        string? request = null, RequestObligations? obligations = null,
        Func<int, int, string?>? beforeRetry = null)
    {
        try
        {
            // Each file once. The reviewer is told which files the run changed so it can judge the
            // report against them, and "README.md, README.md, README.md, README.md" says four
            // things happened where one did.
            string[] changed;
            lock (artifacts)
                changed = FilesTouched(artifacts);

            var written = (changes is not null && before is not null
                              ? await MeasuredChangesAsync(changes, before, journal.Actions.Skip(stepStart).ToArray(), ct)
                              : null)
                          ?? await ReadWrittenAsync(store, ct);
            return await _stepReview.ExecuteAsync(title, LastAssistant(convo), journal,
                evidenceStart, stepStart, changed, written, reviewProvider, reviewModel, ct,
                request, obligations, beforeRetry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StepReview.Failure(ex);
        }
    }

}
