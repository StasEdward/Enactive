namespace Enactive.Server;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

public sealed class HostConnections
{
    private readonly ConcurrentDictionary<string, (string Host, Action Abort)> _connections = new();
    public void Add(string connection, string host, Action abort) => _connections[connection] = (host, abort);
    public void Remove(string connection) => _connections.TryRemove(connection, out _);
    public void Revoke(string host)
    {
        foreach (var entry in _connections.Where(x => x.Value.Host == host)) entry.Value.Abort();
    }
}

[Authorize(AuthenticationSchemes = "Host")]
public sealed class HostHub(GatewayService service, HostConnections connections) : Hub
{
    public override Task OnConnectedAsync()
    {
        connections.Add(Context.ConnectionId, Context.UserIdentifier!, Context.Abort);
        return base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    // Host calls Sync every 15 seconds. Commands remain pending until durably accepted by Host.
    public List<RemoteCommand> Sync(List<Workspace> workspaces) => Invoke(() => service.Sync(Context.UserIdentifier!, workspaces));
    public bool Acknowledge(string commandId) => Invoke(() => { service.Acknowledge(Context.UserIdentifier!, commandId); return true; });
    public bool Publish(HostEvent ev) => Invoke(() => { service.Publish(Context.UserIdentifier!, ev); return true; });

    private static T Invoke<T>(Func<T> action)
    {
        try { return action(); }
        catch (ApiError error) { throw new HubException(error.Message); }
    }
}
