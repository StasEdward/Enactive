namespace Enactive.Remote.Contracts;

/// <summary>The protocol these contracts describe. Reported by the gateway's health endpoint.</summary>
public static class RemoteProtocol
{
    public const int Version = 1;
}

/// <summary>What the owner asked the Host to do. The payload is read according to this.</summary>
public enum CommandKind
{
    StartTask,
    CancelRun,
    ResolveApproval
}

/// <summary>
/// Where a command is in its delivery, which is NOT where the work is.
/// <see cref="AcceptedByHost"/> means the Host wrote it down, not that it ran it - the whole point
/// of a separate acknowledgement is that those two are different moments and a crash can land
/// between them.
/// </summary>
public enum CommandStatus
{
    PendingDelivery,
    AcceptedByHost,
    Expired,
    Rejected
}

/// <summary>
/// The gateway's view of a run. A projection of what the Host reported, never a second opinion:
/// the Host is authoritative about what actually happened on the machine.
/// </summary>
public enum RemoteRunStatus
{
    Queued,
    Running,
    WaitingForUser,
    CancelRequested,
    Completed,
    Failed,
    Incomplete,
    Cancelled,
    Interrupted
}

/// <summary>
/// What the Host is telling the gateway.
///
/// <para><see cref="Progress"/> is the only kind that does not move the run's state; every other
/// kind is a transition, which is why they are checked rather than recorded.</para>
///
/// <para><see cref="Interrupted"/> is published by recovery on startup, for a run that was in
/// flight when the application stopped. It says "this may have done something and nobody watched
/// the end", which is the honest answer and not the same as failure.</para>
/// </summary>
public enum RemoteEventKind
{
    Running,
    Progress,
    ApprovalRequested,
    ApprovalResolved,
    Completed,
    Failed,
    Incomplete,
    Cancelled,
    Interrupted
}

/// <summary>
/// Where a permission request is.
///
/// <para><see cref="DecisionQueued"/> exists because an owner's answer is not an outcome. It is a
/// command that still has to reach the Host, and the Host may refuse it - the request may have
/// been answered on the desktop first, or expired, or the run may be gone. Collapsing the two
/// would let the panel show "allowed" for an action that never ran.</para>
/// </summary>
public enum ApprovalStatus
{
    Pending,
    DecisionQueued,
    Allowed,
    Denied,
    Expired,
    Invalidated
}

/// <summary>
/// How a permission request ended, as the Host reports it.
///
/// <para>Separate from <see cref="ApprovalStatus"/> on purpose: a status includes the two states
/// that are not endings, and an event that carries an ending must not be able to say "Pending".
/// It is also separate from the event's Detail, which is a sentence for a person - the preview
/// used one field for both and a display string was parsed back into state.</para>
/// </summary>
public enum ApprovalOutcome
{
    Allowed,
    Denied,
    Expired,
    Invalidated
}

/// <summary>What the owner clicked. Only two things; everything else is an outcome, not an answer.</summary>
public enum RemoteDecision
{
    Allow,
    Deny
}

/// <summary>
/// The transitions that are decided by the shape of the vocabulary rather than by a rule.
/// </summary>
public static class RunLifecycle
{
    private static readonly RemoteRunStatus[] TerminalStatuses =
    [
        RemoteRunStatus.Completed,
        RemoteRunStatus.Failed,
        RemoteRunStatus.Incomplete,
        RemoteRunStatus.Cancelled,
        RemoteRunStatus.Interrupted
    ];

    private static readonly RemoteEventKind[] TerminalKinds =
    [
        RemoteEventKind.Completed,
        RemoteEventKind.Failed,
        RemoteEventKind.Incomplete,
        RemoteEventKind.Cancelled,
        RemoteEventKind.Interrupted
    ];

    /// <summary>A run that has ended. Nothing reopens one - a later event about it is refused.</summary>
    public static bool IsTerminal(RemoteRunStatus status) => Array.IndexOf(TerminalStatuses, status) >= 0;

    /// <summary>An event that ends the run it belongs to.</summary>
    public static bool IsTerminal(RemoteEventKind kind) => Array.IndexOf(TerminalKinds, kind) >= 0;

    /// <summary>
    /// The status a terminal event leaves behind. Every terminal kind is named identically to its
    /// status on purpose, so this cannot drift - and the pairing is asserted by a test rather than
    /// trusted, because "the names match" is a fact about today's enum, not a guarantee.
    /// </summary>
    public static RemoteRunStatus StatusOf(RemoteEventKind kind)
        => IsTerminal(kind)
            ? Enum.Parse<RemoteRunStatus>(kind.ToString())
            : throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Only a terminal event decides a run's final status.");

    /// <summary>The status a resolved request settles into.</summary>
    public static ApprovalStatus StatusOf(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.Allowed => ApprovalStatus.Allowed,
        ApprovalOutcome.Denied => ApprovalStatus.Denied,
        ApprovalOutcome.Expired => ApprovalStatus.Expired,
        ApprovalOutcome.Invalidated => ApprovalStatus.Invalidated,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown approval outcome.")
    };
}
