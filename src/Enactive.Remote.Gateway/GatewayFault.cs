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

    /// <summary>
    /// The refusal as the person's API answers it: the status, and the code with the message. Every
    /// refusal there takes this one shape - a thrown fault, the cookie scheme's, a limit's - so the panel
    /// reads one kind of body and never has to guess at an empty one.
    /// </summary>
    public Task WriteAsync(HttpContext context)
    {
        context.Response.StatusCode = Status;
        return context.Response.WriteAsJsonAsync(new { code = Code, error = Message });
    }

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

    /// <summary>
    /// A field that should be sealed and is not an envelope of the allowed size. The gateway cannot
    /// open one, only check its shape - and something that is not even the shape is either plaintext
    /// that was never meant to reach it or garbage, and neither is worth storing or retrying.
    /// </summary>
    public static GatewayFault EnvelopeMalformed(string field, int maxChars) => new(
        FaultCode.EnvelopeMalformed, 400,
        $"'{field}' must be a sealed envelope of at most {maxChars:N0} characters.");

    public static GatewayFault UnknownEventKind(string kind) => new(
        FaultCode.UnknownEventKind, 400, $"'{kind}' is not an event kind this gateway understands.");

    public static GatewayFault UnknownRun(string runId) => new(
        FaultCode.UnknownRun, 404, $"Run {runId} does not belong to this Host, or does not exist.");

    public static GatewayFault UnknownApproval(string approvalId) => new(
        FaultCode.UnknownApproval, 404,
        $"Approval {approvalId} does not belong to this run, or does not exist.");

    /// <summary>
    /// A computer whose row is not there. Said as what happened, because it is what the desktop shows when it
    /// stops: the row goes when the person removes the computer's registration, and with the whole account
    /// when they delete it. "This device is not registered" read as a computer that had never been set up.
    /// </summary>
    public static GatewayFault UnknownHost() => new(
        FaultCode.UnknownHost, 404,
        "This computer is not registered on the service any more - its account or its registration was removed.");

    public static GatewayFault HostRevoked() => new(
        FaultCode.HostRevoked, 403, "This device's credential has been revoked.");

    public static GatewayFault AccountDisabled() => new(
        FaultCode.AccountDisabled, 403, "The account this computer belongs to has been disabled.");

    /// <summary>
    /// The same refusal for the person themselves, at sign-in: one code for one fact, in words meant
    /// for the person rather than for their computer.
    /// </summary>
    public static GatewayFault AccountDisabledForPerson() => new(
        FaultCode.AccountDisabled, 403, "This account has been disabled.");

    public static GatewayFault ProtocolMismatch() => new(
        FaultCode.ProtocolMismatch, 400,
        "This computer and the service speak different versions - update Enactive.");

    /// <summary>
    /// A computer calling faster than its rate allows. The quota code, which a Host retries: its
    /// allowance refills every second, and the event it was sending is kept rather than lost.
    /// </summary>
    public static GatewayFault TooManyCalls() => new(
        FaultCode.QuotaExceeded, 429,
        "This computer is calling the service more often than it allows. It will try again shortly.");

    /// <summary>
    /// A grant that is not the shape of one, or that may not come from the caller. Both a computer and a
    /// browser can be refused with it.
    /// </summary>
    public static GatewayFault BadGrant(string message) => new(FaultCode.BadGrant, 400, message);

    /// <summary>
    /// An invitation that is not the caller's, or not there at all, in the same words for both: a refusal
    /// that differed would answer "does somebody else have an invitation by that id?".
    /// </summary>
    public static GatewayFault UnknownInvite() => new(
        FaultCode.UnknownInvite, 404, "That invitation does not exist.");

    public static GatewayFault InviteLimit(int max) => new(
        FaultCode.InviteLimit, 409,
        $"This account already has {max:N0} open {(max == 1 ? "invitation" : "invitations")}. "
        + "Wait for one to be used or to expire - each lasts ten minutes - to make another.");

    /// <summary>
    /// The invitation was answered already. Its own code, not a plain conflict: the new device that lost the
    /// race tells the person to ask for a new invitation, which is the only thing that helps.
    /// </summary>
    public static GatewayFault InviteUsed() => new(
        FaultCode.InviteUsed, 409, "That invitation has already been used. Ask for a new one.");

    public static GatewayFault InviteExpired() => new(
        FaultCode.InviteExpired, 410, "That invitation has expired. Ask for a new one.");

    // ── the ones only the owner API produces ────────────────────────────────
    //
    // These have no FaultCode: nothing on a Host's retry path can reach them, and inventing codes a
    // Host will never classify would only make the table look more complete than it is.

    public static GatewayFault BadRequest(string message) => new("bad-request", 400, message);

    public static GatewayFault Unauthenticated() => new("unauthenticated", 401, "Sign in to continue.");

    public static GatewayFault Forbidden() => new("forbidden", 403, "This account may not do that.");

    /// <summary>
    /// Deleting the account from a session opened too long ago (<c>AccountDeletion.RecentSignIn</c>). Its own
    /// code, not a plain 403: the panel answers it by signing the person out and asking them to sign in again,
    /// which is the only thing that helps.
    /// </summary>
    public static GatewayFault Reauthenticate() => new(
        "reauthenticate", 403, "Sign in again to delete the account.");

    public static GatewayFault RateLimited() => new(
        "rate-limited", 429, "Too many requests. Wait a moment and try again.");

    /// <summary>
    /// A public key that is not an uncompressed P-256 point: wrong text encoding, wrong length, wrong
    /// prefix, or off the curve. Refused here because a grant sealed to it could never be opened.
    /// </summary>
    public static GatewayFault BadKey() => new(
        "bad-key", 400, "The device key must be an uncompressed P-256 public key, as base64url text.");

    /// <summary>
    /// The browser that made this call has been removed from the account. Its own code, not a plain 403:
    /// the panel answers it by telling the person this device was removed, not by retrying.
    /// </summary>
    public static GatewayFault DeviceRevoked() => new(
        "device-revoked", 403, "This device was removed from your account.");

    public static GatewayFault DeviceLimit(int max) => new(
        "device-limit", 409,
        $"This account already has {max:N0} {(max == 1 ? "device" : "devices")}. "
        + "Remove one that is no longer used to add another.");

    /// <summary>
    /// An account at one of its limits (see <c>Limits</c>). A state the person resolves by removing
    /// something or waiting, not a rate a client should retry at once: so 409, where a computer calling
    /// too often is a 429 under the same code. One sentence for every limit, naming the number, so the
    /// person learns what the ceiling is rather than only that there is one.
    /// </summary>
    public static GatewayFault QuotaExceeded(
        string things, long limit, string remedy = "remove one to add another") => new(
        FaultCode.QuotaExceeded, 409,
        $"This account already has {limit:N0} {things}, the most it may; {remedy}.");

    /// <summary>
    /// The account's sealed tasks and history would pass its storage limit. Its own sentence, in megabytes and
    /// with what frees the space: a count of bytes means little to a person, and "already has" was untrue -
    /// it is the refused task or start that would pass the limit.
    /// </summary>
    public static GatewayFault StorageFull(long limitBytes, int retentionDays) => new(
        FaultCode.QuotaExceeded, 409,
        $"This would take this account past its {limitBytes / (1024.0 * 1024.0):0.#} MB of stored tasks and "
        + $"history, the most it may; older runs are removed after {retentionDays} days.");

    /// <summary>
    /// A device-bound call that does not say which device it is made from. One code here so every such
    /// endpoint answers it the same way, and the panel can tell it from an unknown device.
    /// </summary>
    public static GatewayFault DeviceHeaderMissing() => new(
        "device-header", 400,
        "This call must name the browser it is made from in the X-Enactive-Device header.");

    public static GatewayFault Conflict(string message) => new("conflict", 409, message);

    public static GatewayFault NotFound(string message) => new("not-found", 404, message);
}
