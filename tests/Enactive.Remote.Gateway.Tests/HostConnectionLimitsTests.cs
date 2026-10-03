namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// How many connections a computer, an account and the whole gateway may hold open (issue #8). A computer's
/// connection is let out from under the gateway's ceiling on requests once it has authenticated, because it
/// lasts as long as the computer is online - and nothing counted it after that: one token opened two hundred
/// and one connections and all of them stayed open.
///
/// <para>Each test has a gateway of its own, so the counts it reads are its own and not its neighbours'.
/// Long polling, as in <see cref="EndToEndTests"/>: the in-process server speaks it without a socket.</para>
/// </summary>
public sealed class HostConnectionLimitsTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    /// <summary>
    /// One token holds two connections: the one it works on and a reconnect that arrived before the old one
    /// was noticed dead. A third closes the OLDEST rather than being refused, so an honest computer that
    /// reconnects always gets in, and a flood with a stolen token holds two places and no more.
    /// </summary>
    [Fact]
    public async Task One_token_holds_at_most_two_connections_and_the_newest_wins()
    {
        await using var gateway = Gateway();
        using var owner = await SignedInAsync(gateway);
        var computer = await RegisterAsync(owner, "Studio PC");

        var opened = new List<HubConnection>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                opened.Add(await OpenAsync(gateway, computer.Token));
            }

            foreach (var earlier in opened.Take(3))
            {
                Assert.True(await ClosesAsync(earlier), "An earlier connection of the computer was left open.");
            }

            foreach (var newest in opened.Skip(3))
            {
                Assert.Equal(HubConnectionState.Connected, newest.State);
                Assert.Null((await HelloAsync(newest)).Fault);
            }
        }
        finally
        {
            await DisposeAllAsync(opened);
        }
    }

    /// <summary>
    /// An account holds at most twice its computers in connections, and past that a NEW connection is
    /// refused - here an account that holds more computers than its limit allows now, because the operator
    /// lowered it after they were registered: two connections of the first fill it, and the second computer
    /// is turned away. Another account is not: the limit is per account, and one person's full account is
    /// nobody else's business.
    /// </summary>
    [Fact]
    public async Task An_account_holds_at_most_twice_its_computers_in_connections()
    {
        DeviceView first, second;
        await using (var registering = Gateway())
        {
            using var alice = await SignedInAsync(registering);
            first = await RegisterAsync(alice, "Studio PC");
            second = await RegisterAsync(alice, "Laptop");
        }

        await using var gateway = Gateway(hostsPerUser: 1);
        using var bob = await SignedInAsync(gateway);
        var bobs = await RegisterAsync(bob, "Bob's PC");

        var opened = new List<HubConnection>();
        try
        {
            opened.Add(await OpenAsync(gateway, first.Token));
            opened.Add(await OpenAsync(gateway, first.Token));

            var refused = await TryOpenAsync(gateway, second.Token);
            opened.Add(refused);
            var neighbour = await OpenAsync(gateway, bobs.Token);
            opened.Add(neighbour);

            Assert.True(await ClosesAsync(refused), "The connection past the account's limit was left open.");
            Assert.All(opened.Take(2), hub => Assert.Equal(HubConnectionState.Connected, hub.State));
            Assert.Null((await HelloAsync(neighbour)).Fault);
        }
        finally
        {
            await DisposeAllAsync(opened);
        }
    }

    /// <summary>
    /// Computers' connections stay outside the ceiling on requests in progress: as many of them are open as
    /// the ceiling has places, each long poll waiting on the gateway, and a person's ordinary request is still
    /// answered. Counted under the ceiling, they would take every place and the panel would be refused.
    /// </summary>
    [Fact]
    public async Task Ordinary_requests_still_get_through_while_computers_are_connected()
    {
        const int computers = RequestLimits.ConcurrentRequests / RequestLimits.ConnectionsPerComputer;
        await using var gateway = Gateway(hostsPerUser: computers);
        using var owner = await SignedInAsync(gateway);

        var tokens = new List<string>();
        for (var i = 0; i < computers; i++)
        {
            tokens.Add((await RegisterAsync(owner, "Computer " + i)).Token);
        }

        var opened = new List<HubConnection>();
        try
        {
            // A few at a time: each one is authenticated against the database, which other suites share.
            foreach (var batch in tokens.SelectMany(token => Enumerable.Repeat(token, RequestLimits.ConnectionsPerComputer)).Chunk(10))
            {
                opened.AddRange(await Task.WhenAll(batch.Select(token => OpenAsync(gateway, token))));
            }

            using var visitor = new PanelClient(gateway);
            using var session = await visitor.Http.GetAsync("/api/session");

            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            Assert.All(opened, hub => Assert.Equal(HubConnectionState.Connected, hub.State));
            Assert.Equal(RequestLimits.ConcurrentRequests, gateway.Services.GetRequiredService<HostConnections>().Count);
        }
        finally
        {
            await DisposeAllAsync(opened);
        }
    }

    /// <summary>
    /// A computer opening connections over and over is refused for a while, even though each new one would
    /// only have closed an older one: every connection costs a credential lookup and a place being made, so
    /// churn is a flood too. Its connection already open is not closed for it, and the person's other computer
    /// still gets in.
    /// </summary>
    [Fact]
    public async Task A_computer_opening_connections_too_often_is_refused_and_another_is_not()
    {
        await using var gateway = Gateway();
        using var owner = await SignedInAsync(gateway);
        var busy = await RegisterAsync(owner, "Busy");
        var quiet = await RegisterAsync(owner, "Quiet");

        var opened = new List<HubConnection>();
        try
        {
            // The bucket refills while the connections are made, so it may take a few more than its size.
            HubConnection? refused = null;
            var admitted = 0;
            while (refused is null && admitted < 2 * RequestLimits.ConnectionsPerMinute)
            {
                var hub = await TryOpenAsync(gateway, busy.Token);
                opened.Add(hub);

                if (await HelloOrClosedAsync(hub))
                {
                    admitted++;
                }
                else
                {
                    refused = hub;
                }
            }

            var lastAdmitted = opened[^2];
            var other = await OpenAsync(gateway, quiet.Token);
            opened.Add(other);

            Assert.NotNull(refused);
            Assert.True(admitted >= RequestLimits.ConnectionsPerMinute, $"Refused after {admitted} connections.");
            Assert.Equal(HubConnectionState.Connected, lastAdmitted.State);
            Assert.Null((await HelloAsync(other)).Fault);
        }
        finally
        {
            await DisposeAllAsync(opened);
        }
    }

    /// <summary>
    /// No count drifts. Connections taken in, one closed for a newer one, and a negotiation never followed by
    /// a connection - the handshake that never happened, which never reached the hub and so took nothing - and
    /// once every connection has ended, every count reads zero. A place not given back on one of those paths
    /// would be a computer, then an account, then the gateway refused for connections that no longer exist.
    /// </summary>
    [Fact]
    public async Task Counters_return_to_zero_when_everything_disconnects()
    {
        await using var gateway = Gateway();
        var counts = gateway.Services.GetRequiredService<HostConnections>();
        using var owner = await SignedInAsync(gateway);
        var studio = await RegisterAsync(owner, "Studio PC");
        var laptop = await RegisterAsync(owner, "Laptop");

        using (var negotiation = new HttpRequestMessage(HttpMethod.Post, "/hubs/host/negotiate?negotiateVersion=1"))
        {
            negotiation.Headers.Authorization = new("Bearer", studio.Token);
            using var negotiated = await gateway.CreateClient().SendAsync(negotiation);
            Assert.Equal(HttpStatusCode.OK, negotiated.StatusCode);
        }

        var opened = new List<HubConnection>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                opened.Add(await OpenAsync(gateway, studio.Token));
            }

            opened.Add(await OpenAsync(gateway, laptop.Token));

            Assert.Equal(2, counts.CountOf(studio.Id));
            Assert.Equal(1, counts.CountOf(laptop.Id));
            Assert.Equal(3, counts.CountInAccount(owner.UserId));
            Assert.Equal(3, counts.Count);
        }
        finally
        {
            await DisposeAllAsync(opened);
        }

        // A disconnect reaches the hub a moment after the client has let go.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (counts.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, counts.Count);
        Assert.Equal(0, counts.CountOf(studio.Id));
        Assert.Equal(0, counts.CountOf(laptop.Id));
        Assert.Equal(0, counts.CountInAccount(owner.UserId));
    }

    /// <summary>
    /// Revoking a computer closes its connections and gives their places back as it does, not when each
    /// close is noticed: the account's count is down by the time the revocation is answered, and its other
    /// computer is left connected. Deleting the account closes its computers the same way.
    /// </summary>
    [Fact]
    public async Task A_revoked_computers_connections_give_their_places_back()
    {
        await using var gateway = Gateway();
        var counts = gateway.Services.GetRequiredService<HostConnections>();
        using var owner = await SignedInAsync(gateway);
        var revoked = await RegisterAsync(owner, "Old laptop");
        var kept = await RegisterAsync(owner, "Studio PC");

        var opened = new List<HubConnection>();
        try
        {
            opened.Add(await OpenAsync(gateway, revoked.Token));
            opened.Add(await OpenAsync(gateway, revoked.Token));
            var other = await OpenAsync(gateway, kept.Token);
            opened.Add(other);
            Assert.Equal(3, counts.CountInAccount(owner.UserId));

            await owner.PostAsync($"/api/hosts/{revoked.Id}/revoke", new { });

            Assert.Equal(0, counts.CountOf(revoked.Id));
            Assert.Equal(1, counts.CountInAccount(owner.UserId));
            Assert.Equal(1, counts.Count);
            Assert.True(await ClosesAsync(opened[0]), "A revoked computer's connection was left open.");
            Assert.True(await ClosesAsync(opened[1]), "A revoked computer's connection was left open.");
            Assert.Null((await HelloAsync(other)).Fault);
        }
        finally
        {
            await DisposeAllAsync(opened);
        }
    }

    /// <summary>
    /// The whole gateway holds at most its ceiling of computers' connections, whoever they belong to: past it a
    /// new connection is refused, and a place given back is taken again. A computer at its own limit still
    /// replaces its oldest connection with a full gateway, since that takes no new place. Asked of the counts
    /// directly, with a ceiling of three: the gateway's own is two thousand connections.
    /// </summary>
    [Fact]
    public void The_gateway_holds_at_most_its_ceiling_of_computer_connections()
    {
        using var counts = new HostConnections(Limits.Unlimited, ceiling: 3);
        var closed = new List<string>();

        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("a1", "host-a", "alice", () => closed.Add("a1")));
        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("a2", "host-a", "alice", () => closed.Add("a2")));
        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("b1", "host-b", "bob", () => closed.Add("b1")));

        Assert.Equal(HostConnections.Refusal.GatewayFull, counts.TryAdd("c1", "host-c", "carol", () => { }));
        Assert.Equal(HostConnections.Refusal.GatewayFull, counts.TryAdd("b2", "host-b", "bob", () => { }));
        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("a3", "host-a", "alice", () => closed.Add("a3")));
        Assert.Equal(["a1"], closed);
        Assert.Equal(3, counts.Count);

        counts.Remove("b1");

        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("c1", "host-c", "carol", () => { }));
        Assert.Equal(3, counts.Count);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> Gateway(int? hostsPerUser = null)
        => TestGateway.Create(database, configure: builder =>
        {
            if (hostsPerUser is { } limit)
            {
                builder.UseSetting(Limits.HostsSetting, limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });

    private static Task<PanelClient> SignedInAsync(WebApplicationFactory<Program> gateway)
        => PanelClient.SignedInAsync(gateway, "owner-" + Guid.NewGuid().ToString("N")[..8]);

    private static Task<DeviceView> RegisterAsync(PanelClient owner, string name)
        => owner.PostAsync<DeviceView>("/api/hosts", new { name });

    /// <summary>A bare hub connection with the computer's token, serialised as both ends serialise.</summary>
    private static HubConnection Hub(WebApplicationFactory<Program> gateway, string token)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(gateway.Server.BaseAddress, "hubs/host"), options =>
            {
                options.Headers["Authorization"] = $"Bearer {token}";
                options.HttpMessageHandlerFactory = _ => gateway.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(options =>
            {
                foreach (var converter in RemoteJson.Options.Converters)
                {
                    options.PayloadSerializerOptions.Converters.Add(converter);
                }

                options.PayloadSerializerOptions.PropertyNamingPolicy = RemoteJson.Options.PropertyNamingPolicy;
                options.PayloadSerializerOptions.UnmappedMemberHandling = RemoteJson.Options.UnmappedMemberHandling;
            })
            .Build();

    /// <summary>
    /// A connection that has been let in. Its first call is answered only after the gateway has finished
    /// taking it in, so the next one a test opens is counted after it, never alongside.
    /// </summary>
    private static async Task<HubConnection> OpenAsync(WebApplicationFactory<Program> gateway, string token)
    {
        var hub = Hub(gateway, token);
        await hub.StartAsync();
        Assert.Null((await HelloAsync(hub)).Fault);
        return hub;
    }

    /// <summary>
    /// A connection that may be turned away. Its handshake is answered before the gateway decides, so the
    /// start may succeed and the connection close after it, or the close may beat the handshake's answer.
    /// </summary>
    private static async Task<HubConnection> TryOpenAsync(WebApplicationFactory<Program> gateway, string token)
    {
        var hub = Hub(gateway, token);

        try
        {
            await hub.StartAsync();
        }
        catch (Exception)
        {
            // Closed before the handshake's answer arrived: refused, and the state says so.
        }

        return hub;
    }

    /// <summary>True when the connection answers a call, false when it was closed instead.</summary>
    private static async Task<bool> HelloOrClosedAsync(HubConnection hub)
    {
        if (hub.State != HubConnectionState.Connected)
        {
            return false;
        }

        try
        {
            return (await HelloAsync(hub)).Fault is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Task<HostReply<bool>> HelloAsync(HubConnection hub)
        => hub.InvokeAsync<HostReply<bool>>("Hello", RemoteProtocol.Version);

    /// <summary>Whether the connection is closed within a few seconds: long polling notices at its next poll.</summary>
    private static async Task<bool> ClosesAsync(HubConnection hub)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (hub.State != HubConnectionState.Disconnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return hub.State == HubConnectionState.Disconnected;
    }

    /// <summary>
    /// A few at a time, like opening them: each close is a request authenticated against the database, and two
    /// hundred at once took a hundred of the server's connections, which every other suite running beside this
    /// one shares - they failed with "Too many connections".
    /// </summary>
    private static async Task DisposeAllAsync(IEnumerable<HubConnection> hubs)
    {
        foreach (var batch in hubs.Chunk(10))
        {
            await Task.WhenAll(batch.Select(hub => hub.DisposeAsync().AsTask()));
        }
    }

    private sealed record DeviceView(string Id, string Name, string Token);
}
