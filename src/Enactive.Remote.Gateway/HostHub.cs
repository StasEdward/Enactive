namespace Enactive.Remote.Gateway;

using System.Collections.Concurrent;
using Enactive.Remote.Contracts;
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
/// The three methods a Host calls. Every one of them takes its Host from the authenticated
/// identity and never from an argument, so "act as another Host" is not a request that can be
/// made rather than one that is refused.
/// </summary>
[Authorize(AuthenticationSchemes = HostAuthentication.SchemeName)]
public sealed class HostHub(HostService hosts, HostConnections connections) : Hub
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

    public Task<HostReply<IReadOnlyList<HostCommand>>> Sync(List<WorkspaceRef> workspaces)
        => Guard(() => hosts.SyncAsync(HostId, workspaces, Context.ConnectionAborted));

    public Task<HostReply<bool>> Acknowledge(string commandId)
        => Guard(async () =>
        {
            await hosts.AcknowledgeAsync(HostId, commandId, Context.ConnectionAborted);
            return true;
        });

    public Task<HostReply<bool>> Publish(HostEvent published)
        => Guard(async () =>
        {
            await hosts.PublishAsync(HostId, published, Context.ConnectionAborted);
            return true;
        });

    private string HostId => Context.UserIdentifier
        ?? throw new HubException("This connection has no identity.");

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
    /// </summary>
    private static async Task<HostReply<T>> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return HostReply<T>.Ok(await action());
        }
        catch (GatewayFault fault)
        {
            return HostReply<T>.Refused(fault.ToContract());
        }
    }
}
