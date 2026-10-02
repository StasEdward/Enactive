namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;

/// <summary>
/// The far end, as the delivery loop needs to see it.
///
/// <para>An interface because the loop's behaviour under refusal is the whole of stage 3, and
/// exercising it against a real server would mean arranging for a real server to refuse things on
/// cue - which tests what the gateway does, not what the Host does about it.</para>
/// </summary>
public interface IGatewayConnection
{
    /// <summary>
    /// Whether the connection is still up. A connection that closed is not reopened underneath its
    /// owner - a new one is made, and says Hello first - so the owner has to be able to see that it
    /// closed rather than learn it from calls failing one by one.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Says which protocol this computer speaks. The first call on every connection: a gateway of
    /// another version refuses it with <see cref="FaultCode.ProtocolMismatch"/> and a sentence for the
    /// person, where every later call would only be refused for a different-looking reason.
    /// </summary>
    Task HelloAsync(int protocolVersion, CancellationToken ct);

    Task<IReadOnlyList<HostCommand>> SyncAsync(IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct);

    Task AcknowledgeAsync(string commandId, CancellationToken ct);

    Task PublishAsync(HostEvent published, CancellationToken ct);

    /// <summary>Grants of this computer's keys to its devices; the gateway stores all of a call or none.</summary>
    Task PublishGrantsAsync(IReadOnlyList<KeyGrant> grants, CancellationToken ct);

    /// <summary>Registers an invitation under an id this computer made; its secret stays here.</summary>
    Task CreateInviteAsync(string inviteId, CancellationToken ct);

    /// <summary>The answers to this computer's invitations that it has not yet said it handled.</summary>
    Task<IReadOnlyList<EnrollmentView>> EnrollmentsAsync(CancellationToken ct);

    /// <summary>The answer to this invitation was handled; the gateway stops handing it over.</summary>
    Task AnsweredInviteAsync(string inviteId, CancellationToken ct);

    /// <summary>
    /// A device of this computer's owner was removed on this computer: the gateway stops serving it - its
    /// calls and the grants it holds - as it does when the person removes it in a browser.
    /// </summary>
    Task RevokeDeviceAsync(string deviceId, CancellationToken ct);
}

/// <summary>
/// A refusal that arrived from the gateway with a code.
///
/// <para>The code is what decides the fate of a queued event, so it is carried as data rather than
/// left in a message. Anything else that goes wrong - a socket closing, a timeout - is an ordinary
/// exception and is treated as <see cref="FaultDisposition.Retry"/>, which is what it is.</para>
/// </summary>
public sealed class GatewayRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public FaultDisposition Disposition => RemoteFaults.DispositionOf(Code);
}

/// <summary>
/// The gateway refused this computer's credential before any call could be made: the device token
/// was revoked, the account is disabled, or the token was never the gateway's.
///
/// <para>It arrives as a bare 401 or 403 at the start of the connection, with no fault code, because
/// authentication runs before the hub. Left as the transport exception it is, it looked like a
/// dropped connection, and the service dialled again with the same credential for as long as the
/// application ran, saying nothing a person could act on.</para>
/// </summary>
public sealed class GatewayCredentialRefusedException(Exception? inner = null) : Exception(Sentence, inner)
{
    public const string Sentence =
        "The service refused this computer's credential - it was revoked or the account is disabled. "
        + "Make a new connection code in the browser and connect this computer with it.";
}
