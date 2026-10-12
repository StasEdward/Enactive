namespace Enactive.Server.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Who the service answers at all (server plan 1.2): the configured subnets, browsers on the names it is reached by,
/// and the Host header those names give - decided before routing, so the browser hub is behind it too.
/// </summary>
public sealed class NetworkGateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-gate", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    /// <summary>The test server has no sockets, so the client's address is taken from a header, ahead of the pipeline.</summary>
    private const string From = "X-Test-Remote";

    private sealed class RemoteFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, go) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[From], out var remote))
                    context.Connection.RemoteIpAddress = remote;
                return go();
            });
            next(app);
        };
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = new ServerOptions
        {
            Listen = ["0.0.0.0:8443"],
            AllowedSubnets = ["10.20.0.0/16", "fd00:20::/32"],
            HostNames = ["localhost", "enactive.lab.local"],
            Certificate = new CertificateOptions(Path: "server.pfx")
        };
        _app = ServerApp.Build([], options, _root, certificate: null, b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddTransient<IStartupFilter, RemoteFromHeader>();
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private Task<HttpResponseMessage> GetAsync(string path, string remote, string? origin = null, string? host = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(From, remote);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("10.20.3.4")]
    [InlineData("::ffff:10.20.3.4")]   // an IPv4 client on a dual-stack listener
    [InlineData("fd00:20::7")]
    public async Task A_client_in_the_configured_subnets_is_answered(string remote)
        => Assert.Equal(HttpStatusCode.OK, (await GetAsync("/health", remote)).StatusCode);

    [Theory]
    [InlineData("10.21.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]   // not even this machine, unless the settings name it
    public async Task A_client_outside_them_gets_nothing(string remote)
    {
        var response = await GetAsync("/", remote);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Before routing: the browser hub is behind the subnet filter as every page is.</summary>
    [Fact]
    public async Task The_browser_hub_is_behind_the_subnets_too()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add(From, "192.168.1.10");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// A page on another site can open a WebSocket to the service in a signed-in person's browser, and the browser
    /// sends their cookie along. Such a request says where it comes from, and is refused.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("http://enactive.lab.local", HttpStatusCode.Forbidden)]   // not over HTTPS
    [InlineData("https://ENACTIVE.lab.local:8443", HttpStatusCode.OK)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    public async Task A_request_from_a_page_elsewhere_is_refused(string origin, HttpStatusCode expected)
        => Assert.Equal(expected, (await GetAsync("/health", "10.20.3.4", origin)).StatusCode);

    /// <summary>A name somebody else points at the service's address (DNS rebinding) is not one it answers to.</summary>
    [Fact]
    public async Task A_host_name_the_service_is_not_reached_by_is_refused()
        => Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("/health", "10.20.3.4", host: "attacker.example")).StatusCode);

    /// <summary>
    /// The subnets come first, whatever the Host says: a network the service does not answer learns nothing from it, not
    /// even that it would refuse a name. The framework's own host filter ran ahead of everything and answered such a
    /// client with its "Invalid Hostname" page.
    /// </summary>
    [Fact]
    public async Task A_client_outside_the_subnets_gets_nothing_whatever_the_host()
    {
        var response = await GetAsync("/health", "192.168.1.10", host: "attacker.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The refusal says why in the service's log: the browser shows only "Invalid Hostname", and a service opened by a
    /// name server.json did not list (localhost, with HostNames naming the machine) looked broken with nothing said.
    /// </summary>
    [Fact]
    public async Task A_refused_host_name_is_said_in_the_log_with_the_names_it_answers()
    {
        var log = _app.Services.GetRequiredService<Enactive.Workspace.LogHub>();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/health", "10.20.3.4", host: "localhost:8443")).StatusCode);
        Assert.DoesNotContain(log.Snapshot(), e => e.Message.Contains("is not a name the service is reached by"));

        var refused = await GetAsync("/health", "10.20.3.4", host: "pcil-0609:8443");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var said = Assert.Single(log.Snapshot(), e => e.Message.Contains("is not a name the service is reached by"));
        Assert.Contains("pcil-0609", said.Message);
        Assert.Contains("localhost, enactive.lab.local", said.Message);
    }
}
