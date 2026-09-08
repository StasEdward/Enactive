namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts;

/// <summary>
/// The far end, as the delivery loop needs to see it.
///
/// <para>An interface because the loop's behaviour under refusal is the whole of stage 3, and
/// exercising it against a real server would mean arranging for a real server to refuse things on
/// cue - which tests what the gateway does, not what the Host does about it.</para>
/// </summary>
public interface IGatewayConnection
{
    Task<IReadOnlyList<HostCommand>> SyncAsync(IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct);

    Task AcknowledgeAsync(string commandId, CancellationToken ct);

    Task PublishAsync(HostEvent published, CancellationToken ct);
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

    /// <summary>
    /// Reads a refusal back out of a hub error.
    ///
    /// <para>SignalR delivers only the message text, so the gateway puts the code at the front of
    /// it. A message with nothing recognisable in front is NOT treated as a coded refusal: an
    /// unrecognised failure is a transport failure until something says otherwise, and that keeps
    /// the safe answer - retry - as the default.</para>
    /// </summary>
    public static GatewayRefusedException? TryRead(string? message)
    {
        var separator = message?.IndexOf(':') ?? -1;

        if (message is null || separator <= 0)
        {
            return null;
        }

        var code = message[..separator].Trim();

        return RemoteFaults.KnownCodes.Contains(code)
            ? new GatewayRefusedException(code, message[(separator + 1)..].Trim())
            : null;
    }
}
