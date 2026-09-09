namespace Enactive.Remote.Gateway;

using Enactive.Remote.Contracts;

/// <summary>
/// A refusal that names itself.
///
/// <para>The preview threw one exception type carrying a sentence, and the hub turned it into a
/// <c>HubException</c> with that sentence as its text. A Host holding a durable outbox then had one
/// question it could not answer: keep this item, or throw it away? "The network hiccuped" and "this
/// run ended an hour ago" arrive identically, and the correct responses are opposites.</para>
///
/// <para>So every refusal carries a <see cref="Code"/> from <see cref="FaultCode"/>. The Host
/// classifies it with <see cref="RemoteFaults.DispositionOf"/> - its own table, not ours, because
/// what a Host does with its own queue is not a decision this end gets to make.</para>
/// </summary>
public sealed class GatewayFault(string code, int status, string message) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The HTTP status for the owner API. The hub ignores it and sends the code.</summary>
    public int Status { get; } = status;

    public RemoteFault ToContract() => new(Code, RemoteFaults.DispositionOf(Code), Message);

    // ── the ones a Host sees ────────────────────────────────────────────────

    public static GatewayFault RunEnded(string runId) => new(
        FaultCode.RunEnded, 409,
        $"Run {runId} has already ended. A terminal run is never reopened by a later event.");

    public static GatewayFault InvalidTransition(RemoteEventKind kind, RemoteRunStatus from) => new(
        FaultCode.InvalidTransition, 409,
        $"A run that is {from} cannot be moved by a {kind} event.");

    public static GatewayFault SequenceAlreadyApplied(long sequence, long applied) => new(
        FaultCode.SequenceAlreadyApplied, 409,
        $"Sequence {sequence} is not ahead of {applied}, which this run has already applied. "
        + "Sequences must increase; they need not be contiguous, because an event the Host dropped "
        + "leaves a gap on purpose.");

    public static GatewayFault ApprovalAlreadyResolved(string approvalId, ApprovalStatus status) => new(
        FaultCode.ApprovalAlreadyResolved, 409,
        $"Approval {approvalId} is {status} and cannot be resolved again.");

    public static GatewayFault ApprovalNotRemotelyDecidable(string approvalId) => new(
        FaultCode.ApprovalNotRemotelyDecidable, 409,
        $"Approval {approvalId} can only be answered on the computer running the task.");

    public static GatewayFault ActionHashMismatch(string approvalId) => new(
        FaultCode.ActionHashMismatch, 409,
        $"The action hash does not match the one approval {approvalId} was raised for, so this "
        + "answer is about a different action.");

    public static GatewayFault CommandExpired(string commandId) => new(
        FaultCode.CommandExpired, 409, $"Command {commandId} expired before it was accepted.");

    public static GatewayFault MalformedEvent(string why) => new(
        FaultCode.MalformedEvent, 400, why);

    public static GatewayFault UnknownEventKind(string kind) => new(
        FaultCode.UnknownEventKind, 400, $"'{kind}' is not an event kind this gateway understands.");

    public static GatewayFault UnknownRun(string runId) => new(
        FaultCode.UnknownRun, 404, $"Run {runId} does not belong to this Host, or does not exist.");

    public static GatewayFault UnknownApproval(string approvalId) => new(
        FaultCode.UnknownApproval, 404,
        $"Approval {approvalId} does not belong to this run, or does not exist.");

    public static GatewayFault UnknownHost() => new(
        FaultCode.UnknownHost, 404, "This device is not registered.");

    public static GatewayFault HostRevoked() => new(
        FaultCode.HostRevoked, 403, "This device's credential has been revoked.");

    // ── the ones only the owner API produces ────────────────────────────────
    //
    // These have no FaultCode: nothing on a Host's retry path can reach them, and inventing codes a
    // Host will never classify would only make the table look more complete than it is.

    public static GatewayFault BadRequest(string message) => new("bad-request", 400, message);

    public static GatewayFault Conflict(string message) => new("conflict", 409, message);

    public static GatewayFault NotFound(string message) => new("not-found", 404, message);
}
