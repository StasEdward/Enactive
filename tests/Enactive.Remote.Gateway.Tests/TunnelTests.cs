namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>
/// Behind a Cloudflare tunnel. Stage 7 of <c>Docs/REMOTE_DESIGN.md</c>.
///
/// <para>`cloudflared` runs on the gateway's own machine and connects OUTWARD, so nothing on the
/// server listens publicly and every request the gateway sees arrives from 127.0.0.1. That one fact
/// breaks something quietly: the login rate limiter partitions by remote address, so without the
/// forwarded header every visitor on earth shares one bucket - and a stranger guessing at the key
/// would lock the owner out of their own panel. A limiter that cannot tell two people apart is a
/// denial of service with a schedule.</para>
///
/// <para>xUnit builds a fresh instance of this class for each test, so each one gets its own
/// gateway and its own empty rate limiter. That matters here more than usual: these tests are
/// ABOUT the limiter's counters, and sharing them would make each test's result depend on which of
/// its neighbours ran first.</para>
/// </summary>
public sealed class TunnelTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private const string OwnerKey = "a-development-owner-key-for-tests";

    /// <summary>The window is a minute and the limit ten, so eleven is one past it.</summary>
    private const int PastTheLimit = 11;

    private WebApplicationFactory<Program> Gateway(bool behindTunnel, string? urls = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ENACTIVE_REMOTE_DB", database.ConnectionString);
            builder.UseSetting("ENACTIVE_OWNER_KEY", OwnerKey);
            builder.UseSetting("ENACTIVE_DATA", Path.Combine(Path.GetTempPath(), database.Name));
            builder.UseSetting("environment", "Development");
            builder.UseSetting(Deployment.BehindTunnelSetting, behindTunnel ? "true" : "false");

            if (urls is not null)
            {
                builder.UseSetting("urls", urls);
            }
        });

    /// <summary>
    /// The control, and the reason the next test means anything.
    ///
    /// <para>Without the forwarded header every caller is 127.0.0.1, so eleven attempts share one
    /// bucket and the eleventh is refused. This is what the deployment does to the limiter, stated
    /// as a fact rather than a worry - and it is also proof that the limiter is switched on, which
    /// the test below would otherwise be unable to distinguish from a limiter that does nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Without_the_header_everybody_shares_one_bucket()
    {
        await using var gateway = Gateway(behindTunnel: true);
        var statuses = await LoginRepeatedlyAsync(gateway, address: _ => null);

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    /// <summary>
    /// And with it, eleven people are eleven people.
    ///
    /// <para>Shown red by turning the tunnel setting off, which leaves the header unread: the
    /// eleventh caller is then refused for what the first ten did.</para>
    /// </summary>
    [Fact]
    public async Task The_address_cloudflare_reports_is_what_the_limiter_counts()
    {
        await using var gateway = Gateway(behindTunnel: true);
        var statuses = await LoginRepeatedlyAsync(gateway, address: i => $"203.0.113.{i + 1}");

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
        var statuses = await LoginRepeatedlyAsync(gateway, address: _ => "203.0.113.7");

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
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
    /// <para>The CORRECT key every time, deliberately. A wrong one would also exercise the global
    /// consecutive-failure lockout, and a test that trips two mechanisms cannot say which one
    /// answered. What is under test here is the per-caller limiter and nothing else.</para>
    ///
    /// <para>A fresh client per attempt, which is what eleven people actually are: eleven browsers,
    /// eleven cookie jars, eleven antiforgery tokens. The first draft reused one client and got ten
    /// 400s - signing in changes the identity the antiforgery token is bound to, so every attempt
    /// after the first was refused for a reason that had nothing to do with rate limiting. The
    /// limiter partitions by address and never looks at a cookie, so this changes nothing about
    /// what is being measured and everything about whether it can be seen.</para>
    /// </summary>
    private static async Task<List<HttpStatusCode>> LoginRepeatedlyAsync(
        WebApplicationFactory<Program> gateway, Func<int, string?> address)
    {
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < PastTheLimit; attempt++)
        {
            using var client = gateway.CreateClient();
            var csrf = (await client.GetFromJsonAsync<SessionView>("/api/session", RemoteJson.Options))!.CsrfToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            {
                Content = JsonContent.Create(new { key = OwnerKey }, options: RemoteJson.Options)
            };

            request.Headers.Add("X-CSRF-TOKEN", csrf);

            if (address(attempt) is { } reported)
            {
                request.Headers.Add(Deployment.ClientAddressHeader, reported);
            }

            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    private sealed record SessionView(bool Authenticated, string CsrfToken);
}
