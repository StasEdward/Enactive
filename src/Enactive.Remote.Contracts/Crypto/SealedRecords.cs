namespace Enactive.Remote.Contracts.Crypto;

using System.Globalization;

/// <summary>
/// What a task's sealed envelope holds: the two fields the gateway must never read. Its id, host and
/// workspace stay plaintext because routing needs them, and the associated data (<see cref="Ad.Task"/>)
/// binds them to this ciphertext.
/// </summary>
public sealed record SealedTask(string Title, string Prompt);

// The authorization records below ride inside a sealed command. A command's plaintext fields are written by
// the gateway, so the Host acts on what is inside the seal and compares it with the plaintext: a gateway
// that rewrote the task id or the approval it points at is caught by the mismatch. IssuedAt is what lets the
// Host refuse a command the gateway kept and replayed long after the owner sent it.

/// <summary>The owner's instruction to start a task, as only the owner could have sealed it.</summary>
public sealed record StartAuthorization(string TaskId, string WorkspaceId, DateTimeOffset IssuedAt);

/// <summary>The owner's instruction to cancel one run.</summary>
public sealed record CancelAuthorization(string RunId, DateTimeOffset IssuedAt);

/// <summary>
/// The owner's answer to one permission request. <see cref="ActionHash"/> is inside the seal so that an
/// "allow" given for one action cannot be attached by the gateway to a different one.
/// </summary>
public sealed record DecisionAuthorization(string ApprovalId, string ActionHash, RemoteDecision Decision, DateTimeOffset IssuedAt);

/// <summary>The owner's instruction to cut one browser off from the Host's keys.</summary>
public sealed record DeviceRevocation(string DeviceId, DateTimeOffset IssuedAt);

/// <summary>
/// The owner's vouching for a new browser. The public key is inside the seal because a gateway that could
/// substitute its own key here would be endorsed as a device.
/// </summary>
public sealed record DeviceEndorsement(string DeviceId, string DevicePublic, string Label, DateTimeOffset IssuedAt);

/// <summary>
/// What a permission request's sealed envelope holds. Both <see cref="FullText"/> (what the card shows) and
/// <see cref="ArgumentsJson"/> (what the action hash covers) travel, so the browser can show one and verify the other.
/// </summary>
public sealed record SealedAction(string Tool, string ArgumentsJson, string FullText, string WorkingDirectory, string Topic);

/// <summary>
/// The associated data for each sealed record (spec §6). AES-GCM authenticates it without hiding it, so a
/// ciphertext only opens under the ids it was sealed for: a gateway cannot move a sealed task to another
/// workspace, a sealed start to another command, or an event's detail to another sequence number. Each record
/// kind has its own version string, so one kind's data can never be read as another's. The JS twin has the
/// same builders in camelCase, pinned to these bytes by the shared vectors.
/// </summary>
public static class Ad
{
    public static byte[] Task(string hostId, string taskId, string workspaceId)
        => Canonical.Bytes("enactive-task-v1", hostId, taskId, workspaceId);

    // The kind is part of the data so a sealed start cannot be replayed as a cancel under the same command id.
    public static byte[] Command(string hostId, string commandId, CommandKind kind)
        => Canonical.Bytes("enactive-cmd-v1", hostId, commandId, kind.ToString());

    public static byte[] Event(string hostId, string runId, long sequence, RemoteEventKind kind)
        => Canonical.Bytes("enactive-event-v1", hostId, runId, Number(sequence), kind.ToString());

    public static byte[] Approval(string hostId, string runId, string approvalId, string toolCallId, string actionHash, bool remoteDecidable)
        => Canonical.Bytes("enactive-approval-v1", hostId, runId, approvalId, toolCallId, actionHash, remoteDecidable ? "1" : "0");

    public static byte[] Workspace(string hostId, string workspaceId)
        => Canonical.Bytes("enactive-workspace-v1", hostId, workspaceId);

    // Invariant culture, because the sequence is part of bytes the browser recomputes: a Host running in a
    // locale with other digits would otherwise seal data no browser could open.
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
