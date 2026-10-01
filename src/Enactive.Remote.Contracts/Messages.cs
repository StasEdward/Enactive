namespace Enactive.Remote.Contracts;

/// <summary>
/// A workspace the Host offers. No path: see <see cref="StartTaskPayload"/>. The name a person gave it
/// is sealed (<c>Ad.Workspace</c>): a folder's name is often a client's or a project's, and the gateway
/// needs only the id to route a task to it.
/// </summary>
public sealed record WorkspaceRef(string Id, string SealedName);

/// <summary>
/// One instruction from the owner, as the Host receives it. <see cref="Payload"/> is JSON read
/// according to <see cref="Kind"/>.
/// </summary>
public sealed record HostCommand(
    string Id,
    string HostId,
    CommandKind Kind,
    string Payload,
    CommandStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Start a run.
///
/// <para>There is no path here and there is not going to be one. The remote side names a workspace
/// the Host already has; it cannot name a folder. That is what keeps "run a task remotely" from
/// being "read any directory on that machine remotely", and it is a property of the contract
/// rather than a check somewhere in the Host.</para>
///
/// <para>Nothing a person wrote is here in the clear. <see cref="SealedTask"/> holds the title and
/// prompt, sealed by the browser for this task and workspace; <see cref="SealedStart"/> holds the
/// owner's authorization, sealed for this command. The plaintext ids are for routing only: the gateway
/// writes them, so the Host acts on what opens and checks the ids against it.</para>
/// </summary>
public sealed record StartTaskPayload(
    string RunId,
    string TaskId,
    string WorkspaceId,
    string SealedTask,
    string SealedStart);

/// <summary>
/// Ask a run to stop. Asking is all this does - the Host reports Cancelled once it has.
/// <see cref="Sealed"/> holds the owner's authorization for this run, so a gateway cannot cancel one
/// on its own.
/// </summary>
public sealed record CancelRunPayload(string RunId, string Sealed);

/// <summary>
/// The owner's answer to a permission request.
///
/// <para><see cref="ActionHash"/> travels with it so the Host can check that the answer is about
/// the action it is holding. Receiving this command is not authorisation: it may arrive after the
/// same request was answered on the desktop, after it expired, or after the run ended, and the
/// Host refuses it in all three cases.</para>
///
/// <para>The decision itself is only inside <see cref="Sealed"/>, with the approval id and hash it
/// was given for. A plaintext Allow could be written by the gateway; a sealed one can only come from
/// a device that holds this computer's key.</para>
/// </summary>
public sealed record ResolveApprovalPayload(
    string ApprovalId,
    string RunId,
    string ActionHash,
    string Sealed);

/// <summary>
/// Revoke or endorse a browser device. Everything, the device id included, is inside
/// <see cref="Sealed"/>: the gateway has no business choosing which device a computer trusts.
/// </summary>
public sealed record DevicePayload(string Sealed);

/// <summary>
/// A permission request, exactly as the owner will see it.
///
/// <para><see cref="SealedAction"/> holds the complete action, not a summary. A person cannot
/// approve what they have not been shown, and a shortened description is how somebody approves a
/// command whose tail they never read. It is sealed under the ids beside it, so the gateway can route
/// the answer without reading what is being asked.</para>
///
/// <para><see cref="RemoteDecidable"/> is false for a shell. The request is still published - the
/// panel should say what is being asked and that the answer has to be given on the computer - but
/// the gateway refuses to accept an answer for it. Enforced there rather than hidden in the front
/// end, because a button that is not rendered is not a boundary.</para>
/// </summary>
public sealed record ApprovalRequest(
    string ApprovalId,
    string ToolCallId,
    string ActionHash,
    bool RemoteDecidable,
    string SealedAction);

/// <summary>How a request ended, and which request it was.</summary>
public sealed record ApprovalResolution(
    string ApprovalId,
    string ActionHash,
    ApprovalOutcome Outcome);

/// <summary>
/// One thing the Host is telling the gateway.
///
/// <para><see cref="Sequence"/> is per run and strictly increasing, assigned when the event is
/// queued. Deduplication by <see cref="EventId"/> stops an event being applied twice; it does
/// nothing about order, and a retried event that lands after a later one would otherwise drive the
/// run's state backwards. The Host publishes one event per run at a time, in order, and the
/// gateway refuses anything that is not the next one.</para>
///
/// <para>The approval fields travel as objects rather than as a dozen nullable strings, so
/// "ApprovalRequested with no tool call id" is a shape that has to be constructed deliberately
/// instead of one that happens by omission.</para>
///
/// <para><see cref="SealedDetail"/> is sealed under this event's run, sequence and kind
/// (<c>Ad.Event</c>), so the gateway cannot show one event's sentence as another's.</para>
/// </summary>
public sealed record HostEvent(
    string EventId,
    string RunId,
    long Sequence,
    RemoteEventKind Kind,
    string? SealedDetail = null,
    ApprovalRequest? Approval = null,
    ApprovalResolution? Resolution = null);
