namespace Enactive.Core.Permissions;

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
    string? RecommendedOptionId,
    string? Subject = null,    // the tool name, so a UI can remember approvals per tool
    string? FullDetail = null) // the COMPLETE action, unabridged — see below
{
    /// <summary>
    /// Everything the decision authorises, in full. <see cref="Detail"/> is a one-line summary and
    /// may be elided; this never is.
    ///
    /// The card used to show only the shortened form, which meant a long shell script was approved
    /// with its tail behind an ellipsis while the whole thing was executed. A person cannot consent
    /// to what they were not shown, so the full text is now carried with the request and the UI is
    /// expected to make all of it reachable before the buttons are usable.
    /// </summary>
    public string FullText => string.IsNullOrEmpty(FullDetail) ? Detail : FullDetail!;
}

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
