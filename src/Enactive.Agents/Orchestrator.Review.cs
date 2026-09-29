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
        RequestObligations? obligations = null, string? handedOn = null,
        IReadOnlyList<SuccessCriterionDefinition>? stepCriteria = null, System.Text.Json.Nodes.JsonObject? handedValues = null)
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

        // The places the report and the handed-on result cite, opened by the engine now and recorded as its
        // own observations, so the reviewer judges a claim about "Program.cs:223" against line 223 and not
        // against whatever part of the file the step happened to read and the evidence happened to keep.
        var cited = CitedPlaces.Observe(LastAssistant(messages) + "\n" + (handedOn ?? ""), _workspace.RootPath,
            () => CitedPlaces.Sweep(_workspace.RootPath));
        foreach (var (place, observed) in cited)
            journal.Record(stepNumber, CitedPlaces.ToolName, JsonSerializer.Serialize(new { cited = place }),
                ActionOutcome.Succeeded, observed, WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // What the plan checks this step on, decided by the engine now - a fact for the reviewer, not a judgement.
        if (stepCriteria is { Count: > 0 } && stepNumber is { } planNo
            && TypedCriteria.OfStep(stepCriteria, planNo - 1, _workspace.RootPath) is { Count: > 0 } checkedNow)
            journal.Record(stepNumber, "engine_checked_step_criteria", "{}", ActionOutcome.Succeeded,
                "Checked by the engine when this step was reviewed - the criteria the plan attached to this step:\n"
                + string.Join("\n", checkedNow.Select(r => $"- {r.Name}: {(r.Outcome == CriterionOutcome.Passed ? "PASS" : r.Outcome == CriterionOutcome.Failed ? "FAIL" : "NOT CHECKED")}"
                    + (string.IsNullOrWhiteSpace(r.Detail) ? "" : $" - {r.Detail}"))),
                WorkspaceEffect.None, origin: ToolCallOrigin.Engine);
        // The file this step handed on as the result a criterion checks: the engine decides that it is there,
        // the reviewer decides whether it is what was asked - so it is shown whole, as it is now, even when the
        // step wrote none of it (run 68f92f: an earlier run's report, handed on as this run's).
        if (stepCriteria is { Count: > 0 } && stepNumber is { } handingNo && handedValues is not null)
            foreach (var field in stepCriteria.Where(c => c.Typed?.PathFromStep == handingNo - 1)
                         .Select(c => c.Typed!.PathFromField!).Distinct(StringComparer.Ordinal))
                if (handedValues[field] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var handedPath)
                    && !string.IsNullOrWhiteSpace(handedPath))
                    journal.Record(stepNumber, "engine_opened_handed_file", JsonSerializer.Serialize(new { field, path = handedPath }),
                        ActionOutcome.Succeeded, TypedCriteria.ShowHanded(handedPath, field, _workspace.RootPath),
                        WorkspaceEffect.None, origin: ToolCallOrigin.Engine);

        if (cited.Count > 0)
            await Emit(EventKind.ContextAssembled,
                $"Opened {cited.Count} place(s) the step's report and result cite, for the review: {string.Join(", ", cited.Select(c => c.Cited))}");
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

            var measured = changes is not null && before is not null
                ? await MeasuredChangesAsync(changes, before, journal.Actions.Skip(stepStart).ToArray(), ct)
                : null;
            IReadOnlyList<WrittenFile> written = measured ?? await ReadWrittenAsync(store, ct);
            // What the step wrote where the comparison does not look - a file git ignores, a folder it skips -
            // is still the step's work. Run 16d57849: the report the step wrote, Docs/DRIFT_ollama.md, is ignored
            // by git; the review saw none of it, and could not confirm "a full summary table" it had not been shown.
            if (measured is not null)
            {
                var shown = measured.Select(w => w.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unmeasured = (await ReadWrittenAsync(store, ct))
                    // Scratch stays out of review, as everywhere: helpers and logs, not the work.
                    .Where(w => !shown.Contains(w.RelativePath.Replace('\\', '/'))
                                && !w.RelativePath.Replace('\\', '/').StartsWith(WorkspaceGuard.ScratchPrefix + "/", StringComparison.OrdinalIgnoreCase))
                    .Select(w => w with { Heading = "WRITTEN by this step where the workspace comparison does not look (a file git "
                                                   + "ignores, or a folder it skips) - how it is NOW:" })
                    .ToArray();
                if (unmeasured.Length > 0) written = [.. measured, .. unmeasured];
            }
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
