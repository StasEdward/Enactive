namespace Enactive.Core.Permissions;

/// <summary>Outcome of evaluating a tool call against a permission policy.</summary>
public enum PermissionDecision { Allow, Ask, Deny }

/// <summary>One option offered at a decision fork.</summary>
public sealed record DecisionOption(string Id, string Label);

/// <summary>
/// The exact action a decision authorises, as values.
///
/// <para>Carried with the request rather than reassembled by whoever answers it. A handler that had
/// to work out which call it was being asked about, from whatever happened to be in scope, would be
/// answering a question it inferred - and the whole point of asking is that the answer is about one
/// specific thing. It is also what a remote answer is bound to: the identity is computed from these
/// five values and compared when the answer comes back, so an answer given to one action cannot
/// authorise another.</para>
/// </summary>
/// <param name="ArgumentsJson">Exactly what the tool will be handed. Not reformatted: a tidied copy
/// is a different string, and therefore a different action.</param>
public sealed record BoundAction(
    Guid RunId,
    string ToolCallId,
    string Tool,
    string ArgumentsJson,
    string WorkingDirectory);

/// <summary>A USER DECISION REQUIRED request (PLAN_v2 §7).</summary>
public sealed record DecisionRequest(
    Guid TaskId,
    string Topic,
    string Detail,
    IReadOnlyList<DecisionOption> Options,
    string? RecommendedOptionId,
    string? Subject = null,    // the tool name, so a UI can remember approvals per tool
    string? FullDetail = null, // the COMPLETE action, unabridged — see below
    bool SessionOnly = false,  // an approval that must not outlive the process — see below
    BoundAction? Action = null) // the exact call this authorises, when there is one
{
    /// <summary>
    /// This request, distinctly from every other.
    ///
    /// <para>Generated here rather than required from callers, so nothing has to be passed at
    /// existing call sites and no request can be created without one. It exists because every way
    /// of answering a request from somewhere other than the same thread needs to say WHICH request
    /// is being answered - and until now the only handle on one was the object itself, which does
    /// not survive leaving the process.</para>
    ///
    /// <para><c>init</c> rather than computed, so <c>with</c> keeps the identity: a request that is
    /// copied with one field changed is still the same question.</para>
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

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

    /// <summary>
    /// Whether an approval of this may be remembered beyond the process.
    ///
    /// <para>Set for a shell (<see cref="Tools.ShellTools"/>). "Allow (workspace)" for
    /// <c>run_command</c> was the same button as for <c>read_file</c> and meant something else
    /// entirely: unlimited command execution on that machine, from one click, for as long as the
    /// workspace exists — with the timeline afterwards showing a decision requested and allowed for
    /// every call, as though somebody were answering.</para>
    ///
    /// <para>A SESSION approval is still offered. It dies with the process, which is the property
    /// that makes it a different thing rather than a smaller one.</para>
    /// </summary>
    public bool MayBeRemembered => !SessionOnly;
}

/// <summary>The user's choice at a fork.</summary>
/// <param name="Because">
/// How the decision was reached, when it was not reached by a person just now: "remembered for this
/// workspace", "remembered for this session". Null is a live answer.
///
/// <para>The event stream said <c>git: allowed</c> either way, and the two are separated only by
/// how many milliseconds passed. On 2026-09-08 that cost a wrong diagnosis: a run was believed to
/// have failed for want of permissions, the timeline showed a decision requested and allowed for
/// every git call, and every one of them had in fact been answered by a standing approval granted
/// minutes earlier. A person who restores an "ask" level cannot tell from the log that nothing is
/// asking them.</para>
/// </param>
public sealed record DecisionOutcome(string OptionId, string? Because = null);

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
