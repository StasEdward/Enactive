namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    /// reconnects always gets in, and a flood with a stolen token holds two places and no more. Each one closed
    /// for a newer one is written to the operator's log under the computer's id - a flood shows there as a run
    /// of them - and never with the token.
    /// </summary>
    [Fact]
    public async Task One_token_holds_at_most_two_connections_and_the_newest_wins()
    {
        var log = new RecordingLog();
        await using var gateway = Gateway(log: log);
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

            Assert.Equal(3, log.Lines.Count(line =>
                line.Level == LogLevel.Information
                && line.Text == $"Computer {computer.Id}: its oldest connection was closed for a newer one."));
            Assert.DoesNotContain(log.Lines, line => line.Text.Contains(computer.Token, StringComparison.Ordinal));
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
        string aliceId;
        await using (var registering = Gateway())
        {
            using var alice = await SignedInAsync(registering);
            aliceId = alice.UserId;
            first = await RegisterAsync(alice, "Studio PC");
            second = await RegisterAsync(alice, "Laptop");
        }

        var log = new RecordingLog();
        await using var gateway = Gateway(hostsPerUser: 1, log: log);
        var counts = gateway.Services.GetRequiredService<HostConnections>();
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

            // The refused one took nothing, and its close gave nothing back that it did not hold.
            Assert.Equal(2, counts.CountInAccount(aliceId));
            Assert.Equal(0, counts.CountOf(second.Id));
            Assert.Equal(3, counts.Count);
            Assert.Contains(log.Lines, line =>
                line.Level == LogLevel.Warning && line.Text.Contains(second.Id, StringComparison.Ordinal));
            Assert.DoesNotContain(log.Lines, line => line.Text.Contains(second.Token, StringComparison.Ordinal));
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
    /// only have closed an older one: every one costs a credential lookup and a connection being set up, so
    /// churn is a flood too. The Host is TOLD - its start fails with the 429 - rather than let in and then
    /// dropped without a word, which it could not tell from a network fault. Its connection already open is not
    /// closed for it, and the person's other computer still gets in.
    /// </summary>
    [Fact]
    public async Task A_computer_opening_connections_too_often_is_refused_and_another_is_not()
    {
        await using var gateway = Gateway();
        using var owner = await SignedInAsync(gateway);
        var busy = await RegisterAsync(owner, "Busy");
        var quiet = await RegisterAsync(owner, "Quiet");

        var computers = new List<SignalRGatewayConnection>();
        try
        {
            // The bucket refills while the connections are made, so it may take a few more than its size.
            HttpRequestException? refused = null;
            var droppedSilently = false;
            var admitted = 0;
            while (refused is null && !droppedSilently && admitted < 2 * RequestLimits.ConnectionsPerMinute)
            {
                var computer = Computer(gateway, busy.Token);
                computers.Add(computer);

                try
                {
                    await computer.StartAsync();
                }
                catch (HttpRequestException refusal)
                {
                    refused = refusal;
                    break;
                }

                try
                {
                    await computer.HelloAsync(RemoteProtocol.Version, CancellationToken.None);
                    admitted++;
                }
                catch (Exception)
                {
                    droppedSilently = true;
                }
            }

            var other = Computer(gateway, quiet.Token);
            computers.Add(other);
            await other.StartAsync();

            Assert.False(droppedSilently, "A connection was let in and then dropped without a word.");
            Assert.Equal(HttpStatusCode.TooManyRequests, refused?.StatusCode);
            Assert.True(admitted >= RequestLimits.ConnectionsPerMinute, $"Refused after {admitted} connections.");
            Assert.True(computers[^3].IsOpen, "The computer's last connection was closed by its refused one.");
            await other.HelloAsync(RemoteProtocol.Version, CancellationToken.None);
        }
        finally
        {
            foreach (var computer in computers)
            {
                await computer.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The negotiation is where a connection starts, and where it is counted: a negotiation never followed by a
    /// connection still costs a credential lookup and a connection the server keeps for a while, so one token
    /// asking again and again is refused - with the coded 429 and when to try again, which a response can still
    /// carry there. The calls and polls of a connection already open name it and are not counted; another
    /// computer's negotiation and a person's request are answered as before.
    /// </summary>
    [Fact]
    public async Task A_computer_negotiating_too_often_is_refused_with_a_code_and_others_are_not()
    {
        await using var gateway = Gateway();
        using var owner = await SignedInAsync(gateway);
        var busy = await RegisterAsync(owner, "Busy");
        var quiet = await RegisterAsync(owner, "Quiet");
        using var http = gateway.CreateClient();

        var working = await OpenAsync(gateway, busy.Token);
        try
        {
            var (answered, refused) = await UntilRefusedAsync(() => NegotiateAsync(http, busy.Token));
            using (refused)
            {
                using var other = await NegotiateAsync(http, quiet.Token);
                using var session = await http.GetAsync("/api/session");

                Assert.NotNull(refused);
                Assert.True(answered + 1 >= RequestLimits.ConnectionsPerMinute, $"Refused after {answered + 1}.");
                Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
                Assert.NotNull(refused.Headers.RetryAfter);
                Assert.Equal("rate-limited", (await refused.Content.ReadFromJsonAsync<ErrorView>(RemoteJson.Options))!.Code);
                Assert.Equal(HttpStatusCode.OK, other.StatusCode);
                Assert.Equal(HttpStatusCode.OK, session.StatusCode);
                Assert.Null((await HelloAsync(working)).Fault);
            }
        }
        finally
        {
            await working.DisposeAsync();
        }
    }

    /// <summary>
    /// A connection can skip the negotiation - a WebSocket opened straight at the hub - and is counted the same
    /// way: left out, it was the way round the count, a socket held open until the handshake's timeout with
    /// nothing counting it. However the missing id is written: SignalR takes an EMPTY id, and a key with no
    /// value, as no id at all and starts a new connection, and the first version of the count asked only
    /// whether the key was there - fifteen of fifteen sockets opened with one token through <c>?id=</c>.
    ///
    /// <para>Real WebSockets. A plain request to the hub is answered 400 whatever its id says, once the count
    /// has let it through, and never shows which ones started a connection.</para>
    /// </summary>
    [Theory]
    [InlineData("hubs/host")]
    [InlineData("hubs/host?id=")]
    [InlineData("hubs/host?id")]
    [InlineData("hubs/host?ID=")]
    public async Task A_connection_opened_without_negotiating_is_counted_too(string address)
    {
        await using var gateway = Gateway();
        using var owner = await SignedInAsync(gateway);
        var computer = await RegisterAsync(owner, "Studio PC");

        var sockets = new List<System.Net.WebSockets.WebSocket>();
        try
        {
            string? refused = null;
            while (refused is null && sockets.Count < 2 * RequestLimits.ConnectionsPerMinute)
            {
                var client = gateway.Server.CreateWebSocketClient();
                client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {computer.Token}";

                try
                {
                    sockets.Add(await client.ConnectAsync(new Uri(gateway.Server.BaseAddress, address), default));
                }
                catch (InvalidOperationException notOpened)
                {
                    refused = notOpened.Message;
                }
            }

            Assert.NotNull(refused);
            Assert.Contains("429", refused, StringComparison.Ordinal);
            Assert.InRange(sockets.Count, RequestLimits.ConnectionsPerMinute, RequestLimits.ConnectionsPerMinute + 1);
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Abort();
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// The server itself holds no more WebSockets than computers may have connections, and a margin. A coarse
    /// backstop under the counts: a socket the hub has closed may still be finishing its close, and one opened
    /// without a negotiation is the server's before the hub has seen it.
    /// </summary>
    [Fact]
    public async Task The_server_holds_no_more_websockets_than_computers_may_have_connections()
    {
        await using var gateway = Gateway();
        using var health = await gateway.CreateClient().GetAsync("/health");

        var kestrel = gateway.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Equal(RequestLimits.UpgradedConnections, kestrel.Limits.MaxConcurrentUpgradedConnections);
        Assert.InRange(RequestLimits.UpgradedConnections, RequestLimits.ComputerConnections + 1, 2L * RequestLimits.ComputerConnections);
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
    /// replaces its oldest connection with a full gateway, since that takes no new place - and says it did, for
    /// the log. Asked of the counts directly, with a ceiling of three: the gateway's own is two thousand.
    /// </summary>
    [Fact]
    public void The_gateway_holds_at_most_its_ceiling_of_computer_connections()
    {
        var counts = new HostConnections(Limits.Unlimited, ceiling: 3);
        var closed = new List<string>();

        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("a1", "host-a", "alice", () => closed.Add("a1"), out _));
        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("a2", "host-a", "alice", () => closed.Add("a2"), out _));
        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("b1", "host-b", "bob", () => closed.Add("b1"), out _));

        Assert.Equal(HostConnections.Refusal.GatewayFull, counts.TryAdd("c1", "host-c", "carol", () => { }, out var none));
        Assert.Equal(0, none);
        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("a3", "host-a", "alice", () => closed.Add("a3"), out var replaced));
        Assert.Equal(1, replaced);
        Assert.Equal(["a1"], closed);
        Assert.Equal(3, counts.Count);

        counts.Remove("b1");

        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("c1", "host-c", "carol", () => { }, out _));
        Assert.Equal(3, counts.Count);
    }

    /// <summary>
    /// An honest reconnect gets in even when the gateway, or the account, is full and the computer holds only the
    /// connection it is replacing - the one that dropped without the gateway noticing. Refused there, a computer
    /// whose single connection had died could not come back until the dead one timed out, while a full account
    /// or gateway would be kept full by the dead. Its oldest is closed instead: the new one takes no new place.
    /// </summary>
    [Fact]
    public void A_computer_holding_one_connection_replaces_it_when_the_gateway_or_account_is_full()
    {
        var gatewayFull = new HostConnections(Limits.Unlimited, ceiling: 3);
        var closed = new List<string>();

        gatewayFull.TryAdd("a1", "host-a", "alice", () => closed.Add("a1"), out _);
        gatewayFull.TryAdd("b1", "host-b", "bob", () => closed.Add("b1"), out _);
        gatewayFull.TryAdd("b2", "host-b", "bob", () => closed.Add("b2"), out _);

        Assert.Equal(HostConnections.Refusal.None, gatewayFull.TryAdd("a2", "host-a", "alice", () => { }, out var replaced));
        Assert.Equal(1, replaced);
        Assert.Equal(["a1"], closed);
        Assert.Equal(1, gatewayFull.CountOf("host-a"));
        Assert.Equal(3, gatewayFull.Count);

        // An account of one computer holds two connections; here its two computers hold one each.
        var accountFull = new HostConnections(Limits.Unlimited with { HostsPerUser = 1 });
        closed.Clear();

        accountFull.TryAdd("a1", "host-a", "alice", () => closed.Add("a1"), out _);
        accountFull.TryAdd("c1", "host-c", "alice", () => closed.Add("c1"), out _);

        Assert.Equal(HostConnections.Refusal.None, accountFull.TryAdd("a2", "host-a", "alice", () => { }, out _));
        Assert.Equal(["a1"], closed);
        Assert.Equal(2, accountFull.CountInAccount("alice"));
        Assert.Equal(HostConnections.Refusal.AccountFull, accountFull.TryAdd("d1", "host-d", "alice", () => { }, out _));
    }

    /// <summary>
    /// A close that throws - the connection was already on its way out - neither keeps its place nor stops the
    /// others from being closed. It did both: the exception left <see cref="HostConnections.CloseAll"/> before the
    /// rest of a revoked computer's connections were closed, and left a newer connection's start with an error.
    /// </summary>
    [Fact]
    public void A_close_that_throws_gives_its_place_back_and_the_others_are_still_closed()
    {
        var counts = new HostConnections(Limits.Unlimited);
        var closed = new List<string>();

        counts.TryAdd("a1", "host-a", "alice", () => throw new ObjectDisposedException("connection"), out _);
        counts.TryAdd("a2", "host-a", "alice", () => closed.Add("a2"), out _);

        counts.CloseAll("host-a");

        Assert.Equal(["a2"], closed);
        Assert.Equal(0, counts.Count);
        Assert.Equal(0, counts.CountInAccount("alice"));

        counts.TryAdd("b1", "host-b", "bob", () => throw new ObjectDisposedException("connection"), out _);
        counts.TryAdd("b2", "host-b", "bob", () => closed.Add("b2"), out _);

        Assert.Equal(HostConnections.Refusal.None, counts.TryAdd("b3", "host-b", "bob", () => { }, out var replaced));
        Assert.Equal(1, replaced);
        Assert.Equal(2, counts.CountOf("host-b"));
        Assert.Equal(2, counts.Count);
    }

    /// <summary>
    /// Connections of one computer arriving at once - a flood does not wait its turn - leave two open and close
    /// every other one, and the counts agree with what is open.
    /// </summary>
    [Fact]
    public void Connections_of_one_computer_arriving_at_once_leave_two_and_close_the_rest()
    {
        const int arriving = 50;
        var counts = new HostConnections(Limits.Unlimited);
        var aborted = 0;
        var replaced = 0;

        Parallel.For(0, arriving, i =>
        {
            Assert.Equal(
                HostConnections.Refusal.None,
                counts.TryAdd("c" + i, "host-a", "alice", () => Interlocked.Increment(ref aborted), out var closedHere));
            Interlocked.Add(ref replaced, closedHere);
        });

        Assert.Equal(2, counts.CountOf("host-a"));
        Assert.Equal(2, counts.CountInAccount("alice"));
        Assert.Equal(2, counts.Count);
        Assert.Equal(arriving - 2, aborted);
        Assert.Equal(arriving - 2, replaced);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> Gateway(int? hostsPerUser = null, RecordingLog? log = null)
        => TestGateway.Create(database, configure: builder =>
        {
            if (hostsPerUser is { } limit)
            {
                builder.UseSetting(Limits.HostsSetting, limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (log is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(log));
            }
        });

    /// <summary>The Host's own client, as the desktop opens it.</summary>
    private static SignalRGatewayConnection Computer(WebApplicationFactory<Program> gateway, string token)
        => new(new Uri(gateway.Server.BaseAddress, "hubs/host"), token, options =>
        {
            options.HttpMessageHandlerFactory = _ => gateway.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
        });

    /// <summary>A negotiation, as a computer's client starts one, and nothing after it.</summary>
    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient http, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/host/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new("Bearer", token);
        return http.SendAsync(request);
    }

    /// <summary>
    /// Sends until one is refused with 429, or twice the rate's worth were not; the bucket refills while they
    /// are sent, so it may take a few more than its size. Answers how many were not refused, and the refusal.
    /// </summary>
    private static async Task<(int Answered, HttpResponseMessage? Refused)> UntilRefusedAsync(
        Func<Task<HttpResponseMessage>> send)
    {
        var answered = 0;
        while (answered < 2 * RequestLimits.ConnectionsPerMinute)
        {
            var response = await send();

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (answered, response);
            }

            response.Dispose();
            answered++;
        }

        return (answered, null);
    }

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

    private sealed record ErrorView(string Code, string Error);

    /// <summary>Every line the gateway wrote to its log, as it reads.</summary>
    private sealed class RecordingLog : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> _lines = new();

        public IReadOnlyList<(LogLevel Level, string Text)> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Writer(_lines);

        public void Dispose()
        {
        }

        private sealed class Writer(System.Collections.Concurrent.ConcurrentQueue<(LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
