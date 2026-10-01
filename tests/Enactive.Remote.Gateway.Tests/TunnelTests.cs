namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>
/// Behind a Cloudflare tunnel. Stage 7 of the remote-access design.
///
/// <para>`cloudflared` runs on the gateway's own machine and connects OUTWARD, so nothing on the
/// server listens publicly and every request the gateway sees arrives from 127.0.0.1. That one fact
/// breaks something quietly: the sign-in rate limiter partitions by remote address, so without the
/// forwarded header every visitor on earth shares one bucket - and one stranger hammering the sign-in
/// would lock everybody else out of theirs. A limiter that cannot tell two people apart is a denial
/// of service with a schedule.</para>
///
/// <para>xUnit builds a fresh instance of this class for each test, so each one gets its own
/// gateway and its own empty rate limiter. That matters here more than usual: these tests are
/// ABOUT the limiter's counters, and sharing them would make each test's result depend on which of
/// its neighbours ran first.</para>
/// </summary>
public sealed class TunnelTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    /// <summary>One sign-in past what one address may make in the limit's minute.</summary>
    private const int PastTheLimit = RequestLimits.AuthPerMinute + 1;

    private WebApplicationFactory<Program> Gateway(bool behindTunnel, string? urls = null)
        => TestGateway.Create(database, configure: builder =>
        {
            builder.UseSetting(Deployment.BehindTunnelSetting, behindTunnel ? "true" : "false");

            if (urls is not null)
            {
                builder.UseSetting("urls", urls);
            }
        });

    /// <summary>
    /// The control, and the reason the next test means anything.
    ///
    /// <para>Without the forwarded header every caller is 127.0.0.1, so every attempt shares one
    /// bucket and the one past the limit is refused. This is what the deployment does to the limiter, stated
    /// as a fact rather than a worry - and it is also proof that the limiter is switched on, which
    /// the test below would otherwise be unable to distinguish from a limiter that does nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Without_the_header_everybody_shares_one_bucket()
    {
        await using var gateway = Gateway(behindTunnel: true);
        var statuses = await SignInRepeatedlyAsync(gateway, address: _ => null);

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    /// <summary>
    /// And with it, twenty-one people are twenty-one people.
    ///
    /// <para>Shown red by turning the tunnel setting off, which leaves the header unread: the last
    /// caller is then refused for what the others did.</para>
    /// </summary>
    [Fact]
    public async Task The_address_cloudflare_reports_is_what_the_limiter_counts()
    {
        await using var gateway = Gateway(behindTunnel: true);
        var statuses = await SignInRepeatedlyAsync(gateway, address: i => $"203.0.113.{i + 1}");

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    /// <summary>
    /// One caller is still one caller. Splitting by a header would be no use if it also let one
    /// person pretend to be eleven - but they cannot, because Cloudflare writes this header and
    /// strips whatever the client sent. What this asserts is the other half: the SAME reported
    /// address is still counted together.
    /// </summary>
    [Fact]
    public async Task One_reported_address_is_still_one_bucket()
    {
        await using var gateway = Gateway(behindTunnel: true);
        var statuses = await SignInRepeatedlyAsync(gateway, address: _ => "203.0.113.7");

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    /// <summary>
    /// Every sign-in route under <c>/auth</c> is limited per address, and only per address: one address
    /// past its limit is refused, in the API's coded shape, and the next address is not. The limit that
    /// counted everyone together - the old shared lockout - let one stranger keep every account out.
    /// </summary>
    [Fact]
    public async Task Auth_is_limited_per_address()
    {
        await using var gateway = Gateway(behindTunnel: true);
        using var http = gateway.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        async Task<HttpResponseMessage> CompleteAsync(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/complete");
            request.Headers.Add(Deployment.ClientAddressHeader, address);
            return await http.SendAsync(request);
        }

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < RequestLimits.AuthPerMinute; attempt++)
        {
            using var response = await CompleteAsync("203.0.113.7");
            statuses.Add(response.StatusCode);
        }

        using var refused = await CompleteAsync("203.0.113.7");
        using var neighbour = await CompleteAsync("203.0.113.8");

        // A redirect to the panel's "that did not work": there is no sign-in to complete.
        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Redirect, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("rate-limited", (await refused.Content.ReadFromJsonAsync<ErrorView>(RemoteJson.Options))!.Code);
        Assert.Equal(HttpStatusCode.Redirect, neighbour.StatusCode);
    }

    /// <summary>
    /// However the per-caller limits divide things up, a flood of requests that are each allowed - a
    /// different address every time, each slow to finish - is capped as a whole, so it cannot hold every
    /// connection the gateway has. The cap is load protection and nothing more: once the flood ends, the
    /// next request is served.
    ///
    /// <para>Each held request is a sign-in whose body has not finished arriving, from an address of its
    /// own, so neither the per-address limit nor the database is what holds it. A held request never
    /// finishes until it is let go, so whichever arrive first fill the ceiling and exactly the surplus
    /// is refused, in whatever order they come. The first version probed the ceiling with a request of
    /// its own while the flood was still arriving, and under a loaded machine the probe and the last
    /// held request raced for the final place.</para>
    /// </summary>
    [Fact]
    public async Task A_flood_of_open_requests_is_capped_as_a_whole()
    {
        const int surplus = 10;
        await using var gateway = Gateway(behindTunnel: true);
        using var browser = new PanelClient(gateway);
        await browser.SessionAsync();
        var release = new TaskCompletionSource();

        var flood = Enumerable.Range(0, RequestLimits.ConcurrentRequests + surplus)
            .Select(i => browser.SendAsync(HttpMethod.Post, "/api/dev/sign-in", configure: request =>
            {
                request.Headers.Add(Deployment.ClientAddressHeader, $"198.51.{i / 250}.{i % 250 + 1}");
                request.Content = new HeldBody(release.Task);
            }))
            .ToList();

        // The refused ones are answered at once; the rest wait to be let go.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (flood.Count(call => call.IsCompleted) < surplus && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var refusedWhileFull = flood.Where(call => call.IsCompleted).Select(call => call.Result.StatusCode).ToList();

        release.SetResult();
        var answers = await Task.WhenAll(flood);
        var statuses = answers.Select(response => response.StatusCode).ToList();
        foreach (var response in answers)
        {
            response.Dispose();
        }

        using var after = await browser.Http.GetAsync("/health");

        Assert.Equal(surplus, refusedWhileFull.Count);
        Assert.All(refusedWhileFull, status => Assert.Equal(HttpStatusCode.TooManyRequests, status));

        // Let go, every one that got in is refused for its empty name: the endpoint answering.
        Assert.Equal(RequestLimits.ConcurrentRequests, statuses.Count(status => status == HttpStatusCode.BadRequest));
        Assert.Equal(surplus, statuses.Count(status => status == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// The gateway refuses to start at all if it would trust that header while listening somewhere
    /// a stranger could reach.
    ///
    /// <para>This is the whole security argument for reading <c>CF-Connecting-IP</c>, and the point
    /// of the test is that it is ENFORCED rather than written down: the header is trustworthy only
    /// while cloudflared is the only thing that can open a connection. Bound to <c>0.0.0.0</c>, the
    /// same process would let anyone connect directly and name themselves whatever they liked - and
    /// nothing about it would look wrong. A gate that enforces nothing while looking configured is
    /// worse than no gate, because somebody trusts it.</para>
    ///
    /// <para>Shown red by removing the call to <c>RequireLoopbackListeners</c>: the gateway then
    /// starts perfectly happily in exactly that configuration.</para>
    /// </summary>
    [Fact]
    public async Task A_tunnelled_gateway_will_not_listen_where_a_stranger_could_reach_it()
    {
        await using var gateway = Gateway(behindTunnel: true, urls: "http://0.0.0.0:8080");

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(Deployment.ClientAddressHeader, refused.Message, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Loopback is what it is for, and it starts.</summary>
    [Fact]
    public async Task Listening_on_loopback_is_what_the_tunnel_expects()
    {
        await using var gateway = Gateway(behindTunnel: true, urls: "http://127.0.0.1:8080;http://localhost:8081");

        using var health = await gateway.CreateClient().GetAsync("/health");

        health.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// And a gateway that is NOT behind a tunnel may listen wherever it likes, because it is not
    /// believing anybody's header about who they are.
    /// </summary>
    [Fact]
    public async Task Without_the_tunnel_setting_the_binding_is_nobody_elses_business()
    {
        await using var gateway = Gateway(behindTunnel: false, urls: "http://0.0.0.0:8080");

        using var health = await gateway.CreateClient().GetAsync("/health");

        health.EnsureSuccessStatusCode();
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Signs in <see cref="PastTheLimit"/> times, each call reporting whatever
    /// <paramref name="address"/> returns for it, and hands back what the gateway answered.
    ///
    /// <para>A valid name every time, deliberately, so every refusal is the limiter's: a test that
    /// could be refused for two reasons cannot say which one answered. What is under test here is the
    /// per-caller limiter and nothing else.</para>
    ///
    /// <para>A fresh client per attempt, which is what eleven people actually are: eleven browsers,
    /// eleven cookie jars, eleven antiforgery tokens. The first draft reused one client and got ten
    /// 400s - signing in changes the identity the antiforgery token is bound to, so every attempt
    /// after the first was refused for a reason that had nothing to do with rate limiting. The
    /// limiter partitions by address and never looks at a cookie, so this changes nothing about
    /// what is being measured and everything about whether it can be seen.</para>
    /// </summary>
    private static async Task<List<HttpStatusCode>> SignInRepeatedlyAsync(
        WebApplicationFactory<Program> gateway, Func<int, string?> address)
    {
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < PastTheLimit; attempt++)
        {
            using var browser = new PanelClient(gateway);
            await browser.SessionAsync();

            var reported = address(attempt);
            using var response = await browser.SendAsync(HttpMethod.Post, "/api/dev/sign-in", new { name = "visitor" },
                configure: request =>
                {
                    if (reported is not null)
                    {
                        request.Headers.Add(Deployment.ClientAddressHeader, reported);
                    }
                });

            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    private sealed record ErrorView(string Code, string Error);

    /// <summary>A JSON body that does not finish arriving until <paramref name="release"/> completes.</summary>
    private sealed class HeldBody : HttpContent
    {
        private readonly Task _release;

        // JSON, or the endpoint refuses the request for its type without waiting for the body at all.
        public HeldBody(Task release)
        {
            _release = release;
            Headers.ContentType = new("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await _release;
            await stream.WriteAsync("{}"u8.ToArray());
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
