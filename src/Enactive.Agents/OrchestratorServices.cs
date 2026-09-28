namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>Trusted, host-supplied agents. Implementations may be called by parallel steps and
/// must not retain mutable run state. Null entries select the normal production implementations.</summary>
public sealed record OrchestratorServices(
    IReviewer? Reviewer = null, ISuccessEvaluator? SuccessEvaluator = null, IHandover? Handover = null);

public interface IReviewer
{
    Task<ReviewResult> ReviewAsync(
        string stepTitle, string coderOutput, string executionEvidence, IReadOnlyList<string> artifacts,
        IChatProvider provider, string model, CancellationToken ct,
        ReviewMode mode = ReviewMode.Execution, IReadOnlyList<WrittenFile>? writtenFiles = null,
        string? request = null, RequestObligations? obligations = null);

    Task<ReviewResult> ReviewWithProofAsync(
        string title, string report, EvidenceView evidence, IReadOnlyList<string> artifacts,
        IReadOnlyList<WrittenFile> files, RequestObligations obligations,
        IChatProvider provider, string model, CancellationToken ct, string? workspaceRoot = null,
        Func<int, int, string?>? beforeRetry = null, ReviewMode mode = ReviewMode.Execution);
}

public interface ISuccessEvaluator
{
    Task<SuccessReport> EvaluateAsync(
        IReadOnlyList<SuccessCriterionDefinition> criteria, IToolRegistry tools,
        IPermissionEngine permissions, PermissionPolicy policy, IDecisionHandler decisions,
        ToolContext context, Guid taskId, CancellationToken ct);
}

public interface IHandover
{
    /// <summary>The note, or why there is none - never a bare null (see <see cref="HandoverResult"/>).</summary>
    Task<HandoverResult> GenerateAsync(IChatProvider provider, ChatRequest step, RunBudget runBudget, CancellationToken ct);
}
