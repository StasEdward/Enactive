namespace Enactive.Remote.Host;

using System.Security.Cryptography;
using System.Text.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;

/// <summary>A start command that opened and agreed with itself: the run to begin, and what the owner wrote.</summary>
public sealed record OpenedStart(string RunId, string TaskId, string WorkspaceId, string Title, string Prompt);

/// <summary>
/// A command this computer will not act on: it did not open, it was sealed for another computer or
/// another command, what is sealed disagrees with the plaintext ids it travelled with, it is older than a
/// command may wait, or it is sealed under a key a removal replaced. The message is a short reason, written
/// to sit inside a sentence.
/// </summary>
public sealed class CommandRefusedException(string reason, bool sendAgain = false) : Exception(reason)
{
    /// <summary>
    /// The command may well be the owner's, sealed under the key a removal has since replaced. A start refused
    /// so is not said to have come from an untrusted device: the person whose phone sent it the moment another
    /// device was removed would be told it was forged.
    /// </summary>
    public bool SendAgain { get; } = sendAgain;
}

/// <summary>
/// The one place the Host seals what it sends and opens what it receives (spec section 6).
///
/// <para>The gateway carries every command and writes every plaintext field of it. So nothing here
/// believes a plaintext field on its own: each command is acted on only for what opened under this
/// computer's key, for this command id and kind, and the plaintext ids are compared with it. A gateway
/// can still drop or delay a command - it is the courier - but it cannot write one, retarget one, or
/// keep one and replay it later.</para>
/// </summary>
public sealed class Sealer(IHostKeys keys, TimeProvider clock)
{
    /// <summary>
    /// How far a browser's clock may be from this computer's, either way. Without it a command sealed
    /// on a phone whose clock runs a few minutes ahead would be refused as coming from the future, and
    /// one that waited the full lifetime would be refused a minute early.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>Why a command sealed under an epoch older than the current one is not acted on.</summary>
    public const string SealedBeforeRemoval = "sealed before a device was removed - send it again";

    // ── sealing what goes out ───────────────────────────────────────────────

    /// <summary>An event's sentence, sealed so that it opens only as this run's event of this sequence and kind.</summary>
    public string Detail(string runId, long sequence, RemoteEventKind kind, string text)
        => keys.Current.SealText(text, Ad.Event(keys.HostId, runId, sequence, kind));

    /// <summary>
    /// A permission request's content. What the card shows and what the action hash covers both go in,
    /// so the browser can show one and verify the other.
    /// </summary>
    public string Action(string runId, string approvalId, string toolCallId, string actionHash, bool remoteDecidable, SealedAction action)
        => keys.Current.SealText(RemoteJson.Serialize(action),
            Ad.Approval(keys.HostId, runId, approvalId, toolCallId, actionHash, remoteDecidable));

    /// <summary>The name a person gave a workspace, sealed under its id.</summary>
    public string WorkspaceName(string workspaceId, string name)
        => keys.Current.SealText(name, Ad.Workspace(keys.HostId, workspaceId));

    // ── opening what comes in ───────────────────────────────────────────────

    /// <summary>
    /// A start, opened twice: the owner's authorization for this command, and the task it names. The
    /// two are sealed separately - the task when it was written, the authorization when it was sent - so
    /// the authorization's ids must equal the payload's, or the gateway has attached a genuine task to
    /// a command that was never about it.
    /// </summary>
    public OpenedStart OpenStart(HostCommand command)
    {
        var payload = Payload<StartTaskPayload>(command);
        if (string.IsNullOrEmpty(payload.RunId))
            throw new CommandRefusedException("it names no run");

        var start = Open<StartAuthorization>(payload.SealedStart, Ad.Command(keys.HostId, command.Id, CommandKind.StartTask));
        Fresh(start.IssuedAt);

        if (!Same(start.TaskId, payload.TaskId))
            throw new CommandRefusedException("the task it carries is not the task the owner started");
        if (!Same(start.WorkspaceId, payload.WorkspaceId))
            throw new CommandRefusedException("the workspace it names is not the one the owner chose");

        var task = Open<SealedTask>(payload.SealedTask, Ad.Task(keys.HostId, payload.TaskId, payload.WorkspaceId));
        return new OpenedStart(payload.RunId, payload.TaskId, payload.WorkspaceId, task.Title, task.Prompt);
    }

    public CancelAuthorization OpenCancel(HostCommand command)
    {
        var payload = Payload<CancelRunPayload>(command);
        var cancel = Open<CancelAuthorization>(payload.Sealed, Ad.Command(keys.HostId, command.Id, CommandKind.CancelRun));
        Fresh(cancel.IssuedAt);

        if (!Same(cancel.RunId, payload.RunId))
            throw new CommandRefusedException("the run it names is not the run the owner asked to stop");

        return cancel;
    }

    /// <summary>
    /// An answer to a permission request. The approval id and the action hash are inside the seal, so an
    /// Allow given for one action cannot be attached by the gateway to another.
    /// </summary>
    public DecisionAuthorization OpenDecision(HostCommand command)
    {
        var payload = Payload<ResolveApprovalPayload>(command);
        var decision = Open<DecisionAuthorization>(payload.Sealed, Ad.Command(keys.HostId, command.Id, CommandKind.ResolveApproval));
        Fresh(decision.IssuedAt);

        if (!Same(decision.ApprovalId, payload.ApprovalId) || !Same(decision.ActionHash, payload.ActionHash))
            throw new CommandRefusedException("the answer was given for a different permission request");

        return decision;
    }

    public DeviceRevocation OpenRevocation(HostCommand command)
    {
        var revocation = Open<DeviceRevocation>(Payload<DevicePayload>(command).Sealed,
            Ad.Command(keys.HostId, command.Id, CommandKind.RevokeDevice));
        Fresh(revocation.IssuedAt);
        return revocation;
    }

    public DeviceEndorsement OpenEndorsement(HostCommand command)
    {
        var endorsement = Open<DeviceEndorsement>(Payload<DevicePayload>(command).Sealed,
            Ad.Command(keys.HostId, command.Id, CommandKind.EndorseDevice));
        Fresh(endorsement.IssuedAt);
        return endorsement;
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>
    /// The gateway writes the payload, so a payload that does not parse is a refusal like any other.
    /// Letting the JSON exception through would make it look like a fault of this computer.
    /// </summary>
    private static T Payload<T>(HostCommand command)
    {
        try
        {
            return RemoteJson.Deserialize<T>(command.Payload);
        }
        catch (JsonException)
        {
            throw new CommandRefusedException("it could not be read");
        }
    }

    private T Open<T>(string? envelope, byte[] associatedData)
    {
        if (string.IsNullOrEmpty(envelope))
            throw new CommandRefusedException("it is not sealed");

        uint epoch;
        try
        {
            epoch = Envelope.EpochOf(envelope);
        }
        catch (CryptographicException)
        {
            throw new CommandRefusedException("it is not sealed");
        }

        // Commands open under the current epoch only, never an older one this computer still holds. Every
        // key holder seals with the same key, so nothing here can tell which device sealed a command: a
        // device removed a minute ago still holds the old epoch, and its commands still queued - or new ones
        // it seals - would otherwise be carried out after its removal. A browser that sealed under the old
        // key only because its grant of the new one had not reached it yet is told to send the command again.
        var current = keys.Current;
        if (epoch < current.Epoch)
            throw new CommandRefusedException(SealedBeforeRemoval, sendAgain: true);
        if (epoch != current.Epoch)
            throw new CommandRefusedException("sealed under a key this computer does not hold");

        string json;
        try
        {
            json = current.OpenText(envelope, associatedData);
        }
        catch (CryptographicException)
        {
            // Wrong key, another computer's id, another command's id or kind, or altered bytes: the
            // associated data makes all of these the same failure, and none of them is the owner.
            throw new CommandRefusedException("it was not sealed for this command on this computer");
        }

        try
        {
            return RemoteJson.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            throw new CommandRefusedException("what was sealed could not be read");
        }
    }

    /// <summary>
    /// The inbox refuses a command id it has seen, but a gateway that kept a genuine command could
    /// still deliver it a week later under its own id before the inbox ever saw it. The time it was
    /// sealed at closes that: nothing older than the lifetime a command may wait is acted on.
    /// </summary>
    private void Fresh(DateTimeOffset issuedAt)
    {
        var now = clock.GetUtcNow();

        if (issuedAt < now - RemoteProtocol.CommandLifetime - ClockSkew)
            throw new CommandRefusedException("it was issued too long ago to act on");
        if (issuedAt > now + ClockSkew)
            throw new CommandRefusedException("it claims to have been issued in the future");
    }

    private static bool Same(string? sealedValue, string? plaintext) => string.Equals(sealedValue, plaintext, StringComparison.Ordinal);
}
