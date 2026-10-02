namespace Enactive.Remote.Gateway;

using System.Collections.Concurrent;
using System.Security.Claims;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// The open connections, so revoking a credential can close them rather than waiting for the Host
/// to notice at its next call.
/// </summary>
public sealed class HostConnections
{
    private readonly ConcurrentDictionary<string, (string HostId, Action Abort)> _open = new();

    public void Add(string connectionId, string hostId, Action abort) => _open[connectionId] = (hostId, abort);

    public void Remove(string connectionId) => _open.TryRemove(connectionId, out _);

    public void CloseAll(string hostId)
    {
        foreach (var entry in _open.Where(e => e.Value.HostId == hostId))
        {
            entry.Value.Abort();
        }
    }
}

/// <summary>
/// The methods a Host calls. Every one of them takes its Host and that Host's owner from the
/// authenticated identity and never from an argument, so "act as another Host", or "act in another
/// person's account", is not a request that can be made rather than one that is refused.
/// </summary>
[Authorize(AuthenticationSchemes = HostAuthentication.SchemeName)]
public sealed class HostHub(
    HostService hosts, DeviceService devices, HostConnections connections, HostCallLimit limit) : Hub
{
    public override Task OnConnectedAsync()
    {
        connections.Add(Context.ConnectionId, HostId, Context.Abort);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// The first thing a Host says: which protocol it speaks. Another version is refused with a code
    /// the Host treats as final and words a person can act on; without this, a Host of another version
    /// would see each of its calls refused for a different-looking reason and keep trying.
    ///
    /// <para>A check the Host asks for, not a gate in front of the other calls: a connection that
    /// never says hello is still served, so a Host that does not call it yet keeps working.</para>
    /// </summary>
    public Task<HostReply<bool>> Hello(int protocolVersion)
        => Guard(() => protocolVersion == RemoteProtocol.Version
            ? Task.FromResult(true)
            : throw GatewayFault.ProtocolMismatch());

    public Task<HostReply<IReadOnlyList<HostCommand>>> Sync(List<WorkspaceRef> workspaces)
        => Guard(() => hosts.SyncAsync(Access, workspaces, Context.ConnectionAborted));

    public Task<HostReply<bool>> Acknowledge(string commandId)
        => Guard(async () =>
        {
            await hosts.AcknowledgeAsync(Access, commandId, Context.ConnectionAborted);
            return true;
        });

    public Task<HostReply<bool>> Publish(HostEvent published)
        => Guard(async () =>
        {
            await hosts.PublishAsync(Access, published, Context.ConnectionAborted);
            return true;
        });

    /// <summary>
    /// Grants of this computer's own keys to its owner's devices: answering a pairing or an invitation, or
    /// a rotation. All of them are stored or none is.
    /// </summary>
    public Task<HostReply<bool>> PublishGrants(List<KeyGrant> grants)
        => Guard(async () =>
        {
            await devices.PublishGrantsAsync(Access, grants, Context.ConnectionAborted);
            return true;
        });

    private string HostId => Context.UserIdentifier
        ?? throw new HubException("This connection has no identity.");

    /// <summary>
    /// Built afresh for each call from the connection's claims. The service re-reads the account and
    /// the computer inside every call's transaction, so what the claims said when the connection
    /// opened only names whose rows to look at; it never vouches that they are still allowed.
    /// </summary>
    private HostAccess Access => new(
        HostId,
        Context.User?.FindFirstValue(HostAuthentication.OwnerClaim)
            ?? throw new HubException("This connection has no owner."));

    /// <summary>
    /// Turns a refusal into an ANSWER rather than an exception.
    ///
    /// <para>An exception carries only its message to the other end, and that message does not
    /// survive: SignalR prefixes it, and outside Development it replaces it entirely. A code put
    /// inside it therefore reaches the Host mangled or not at all - and a Host that cannot read the
    /// code treats the refusal as a transport failure and retries it for ever, which is exactly the
    /// right rule applied to the wrong information. Found by the end-to-end test, which is the
    /// first thing that ever ran both halves against each other.</para>
    ///
    /// <para>An unexpected exception still escapes as one. It is not a refusal, nothing about it is
    /// classifiable, and dressing it up as a coded answer would tell the Host something false.</para>
    ///
    /// <para>Every call is counted against this computer's limit here, first, so a refused one costs no
    /// database work and comes back coded like any other refusal.</para>
    /// </summary>
    private async Task<HostReply<T>> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            limit.Take(HostId);
            return HostReply<T>.Ok(await action());
        }
        catch (GatewayFault fault)
        {
            return HostReply<T>.Refused(fault.ToContract());
        }
    }
}
