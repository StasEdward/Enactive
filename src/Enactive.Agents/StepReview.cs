namespace Enactive.Agents;

using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;

/// <summary>The review decision for an already executed step. Evidence and step boundaries are
/// separate: an earlier command may support the answer without changing this step's review mode.</summary>
internal sealed class StepReview(IReviewer reviewer, IToolRegistry tools, string workspaceRoot,
    int evidenceBudget, bool reviewContent, bool checkSoundness)
{
    internal bool ChecksSoundness => checkSoundness;
    internal Task<ReviewResult> ReconcileAsync(string report, EvidenceView evidence,
        IReadOnlyList<string> artifacts, IReadOnlyList<WrittenFile> files, RequestObligations obligations,
        IChatProvider provider, string model, CancellationToken ct, Func<int, int, string?> beforeRetry)
        => reviewer.ReviewWithProofAsync("Final reconciliation of the original request", report, evidence,
            artifacts, files, obligations.ForFinalReview(), provider, model, ct, workspaceRoot, beforeRetry);

    internal async Task<(ReviewResult Result, ReviewMode Mode)> ExecuteAsync(
        string title, string report, ExecutionJournal journal, int evidenceStart, int stepStart,
        IReadOnlyList<string> artifacts, IReadOnlyList<WrittenFile> written,
        IChatProvider provider, string model, CancellationToken ct,
        string? request = null, RequestObligations? obligations = null,
        Func<int, int, string?>? beforeRetry = null)
    {
        try
        {
            var evidence = journal.Describe(evidenceStart, evidenceBudget);
            string[] Names(ToolKind kind) => tools.Definitions.Where(t => t.Kind == kind).Select(t => t.Name).ToArray();
            var composedNothing = journal.UsedOnly(Names(ToolKind.Relocate), stepStart);
            var mode = reviewContent && !journal.UsedAny(Names(ToolKind.Command), stepStart)
                && !composedNothing && written.Count > 0 ? ReviewMode.Content : ReviewMode.Execution;

            var result = checkSoundness && mode == ReviewMode.Execution
                ? await reviewer.ReviewWithProofAsync(title, report, evidence, artifacts, written,
                    obligations ?? RequestObligations.Create(request ?? title, title),
                    provider, model, ct, workspaceRoot, beforeRetry)
                : await reviewer.ReviewAsync(title, report, evidence.Text, artifacts, provider, model, ct,
                    mode, written, request, obligations);
            return (result, mode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure(ex); }
    }

    internal static (ReviewResult Result, ReviewMode Mode) Failure(Exception ex)
        => (new ReviewResult(false, "review error: " + ex.Message)
            { IncompleteReason = "review error: " + ex.Message, VerdictUnavailable = true }, ReviewMode.Execution);
}
