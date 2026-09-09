namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The real far end: a SignalR connection this machine opens OUTWARDS to the gateway.
///
/// <para>Outwards is the whole point. Nothing listens on this computer, no port is forwarded, and a
/// firewall that allows ordinary web traffic is all it needs - which is why a Host can sit behind a
/// home router and still be reachable from a phone.</para>
///
/// <para>The credential travels in a header and never in the URL. SignalR will happily put an
/// access token in the query string, and a URL is the one place a secret is guaranteed to be
/// written down: proxy logs, browser history, a referer.</para>
/// </summary>
public sealed class SignalRGatewayConnection : IGatewayConnection, IAsyncDisposable
{
    private readonly HubConnection _connection;

    /// <param name="configure">
    /// Applied last, so a test can point the client at a test server. Nothing production needs it.
    /// </param>
    public SignalRGatewayConnection(
        Uri hubUrl, string deviceToken, Action<HttpConnectionOptions>? configure = null)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.Headers["Authorization"] = $"Bearer {deviceToken}";
                configure?.Invoke(options);
            })
            .AddJsonProtocol(options =>
            {
                // The same options the gateway uses. Two ends that serialise differently agree on
                // every message until the first enum or the first field one of them has not heard
                // of - and then disagree in a way that looks like a protocol bug rather than a
                // configuration one.
                foreach (var converter in RemoteJson.Options.Converters)
                {
                    options.PayloadSerializerOptions.Converters.Add(converter);
                }

                options.PayloadSerializerOptions.PropertyNamingPolicy = RemoteJson.Options.PropertyNamingPolicy;
                options.PayloadSerializerOptions.UnmappedMemberHandling = RemoteJson.Options.UnmappedMemberHandling;
            })
            .WithAutomaticReconnect()
            .Build();
    }

    /// <summary>Whether the connection is up. A Host that is not connected is not a Host that failed.</summary>
    public bool Connected => _connection.State == HubConnectionState.Connected;

    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    public async Task<IReadOnlyList<HostCommand>> SyncAsync(
        IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct)
        => await InvokeAsync<IReadOnlyList<HostCommand>>("Sync", ct, workspaces) ?? [];

    public Task AcknowledgeAsync(string commandId, CancellationToken ct)
        => InvokeAsync<bool>("Acknowledge", ct, commandId);

    public Task PublishAsync(HostEvent published, CancellationToken ct)
        => InvokeAsync<bool>("Publish", ct, published);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>
    /// One hub call, with the refusal taken out of the ANSWER.
    ///
    /// <para>A refusal arrives as a value, not as an exception - see <see cref="HostReply{T}"/> for
    /// why the exception's message could never carry it. Anything that does arrive as an exception
    /// is therefore not a refusal at all: a closed socket, a server restarting, a bug. It is left
    /// to propagate and the caller treats it as a retry, which is the right answer to "something
    /// went wrong and nobody said what".</para>
    /// </summary>
    private async Task<T?> InvokeAsync<T>(string method, CancellationToken ct, params object?[] arguments)
    {
        var reply = await _connection.InvokeCoreAsync<HostReply<T>>(method, arguments, ct);

        return reply.Fault is { } fault
            ? throw new GatewayRefusedException(fault.Code, fault.Message)
            : reply.Value;
    }
}
