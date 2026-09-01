namespace AIClient.Core.Permissions;

/// <summary>Outcome of evaluating a tool call against a permission policy.</summary>
public enum PermissionDecision { Allow, Ask, Deny }

/// <summary>One option offered at a decision fork.</summary>
public sealed record DecisionOption(string Id, string Label);

/// <summary>A USER DECISION REQUIRED request (PLAN_v2 §7).</summary>
public sealed record DecisionRequest(
    Guid TaskId,
    string Topic,
    string Detail,
    IReadOnlyList<DecisionOption> Options,
    string? RecommendedOptionId);

/// <summary>The user's choice at a fork.</summary>
public sealed record DecisionOutcome(string OptionId);

/// <summary>
/// Handles a decision fork. The orchestrator awaits this when a tool needs approval; the console
/// implements it by prompting, and a future UI implements it by showing a card and awaiting a click.
/// </summary>
public interface IDecisionHandler
{
    Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct);
}

/// <summary>Decides whether a tool call is allowed, needs approval, or is blocked.</summary>
public interface IPermissionEngine
{
    PermissionDecision Evaluate(PermissionPolicy policy, string toolName, PermissionLevel requiredLevel);
}
