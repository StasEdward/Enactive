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

    public Task<IReadOnlyList<HostCommand>> Sync(List<WorkspaceRef> workspaces)
        => Guard(() => hosts.SyncAsync(HostId, workspaces, Context.ConnectionAborted));

    public Task<bool> Acknowledge(string commandId)
        => Guard(async () =>
        {
            await hosts.AcknowledgeAsync(HostId, commandId, Context.ConnectionAborted);
            return true;
        });

    public Task<bool> Publish(HostEvent published)
        => Guard(async () =>
        {
            await hosts.PublishAsync(HostId, published, Context.ConnectionAborted);
            return true;
        });

    private string HostId => Context.UserIdentifier
        ?? throw new HubException("This connection has no identity.");

    /// <summary>
    /// Turns a refusal into something a Host can act on.
    ///
    /// <para>The message a <see cref="HubException"/> carries is the only thing that survives to
    /// the other end, so the fault CODE goes in it, first and machine-readable. Without that a Host
    /// holding a durable outbox cannot tell "retry this" from "never send this again" - which is
    /// the whole reason <see cref="FaultCode"/> exists.</para>
    /// </summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (GatewayFault fault)
        {
            throw new HubException($"{fault.Code}: {fault.Message}");
        }
    }
}
