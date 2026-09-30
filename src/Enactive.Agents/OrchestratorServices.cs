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
    ISuccessEvaluator? SuccessEvaluator = null, IHandover? Handover = null);

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
    /// <param name="promptTokens">The conversation's size as the caller measured it, when it has; otherwise it is estimated.</param>
    Task<HandoverResult> GenerateAsync(IChatProvider provider, ChatRequest step, RunBudget runBudget, CancellationToken ct,
        int? promptTokens = null);
}
