namespace Enactive.Remote.Gateway.Tests;

using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Host;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The two halves, talking to each other. Stage 6a of <c>Docs/REMOTE_DESIGN.md</c>.
///
/// <para><b>Why this exists as its own stage.</b> Every test before it proved one side against a
/// stand-in for the other: the gateway against a database, the Host against a fake gateway, the
/// runner against a fake engine. All of them can pass while the two ends disagree about something
/// neither one can see alone - how an enum is written on the wire, where a fault code lives in a
/// hub error, whether the credential is read from the header the client puts it in. "Five stages
/// done" meant five halves that had never been fitted together.</para>
///
/// <para>Long polling rather than WebSockets, because the in-process test server speaks it without
/// a socket. The transport is not what is under test here; the protocol above it is.</para>
/// </summary>
public sealed class EndToEndTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string OwnerKey = "a-development-owner-key-for-tests";

    private WebApplicationFactory<Program> _gateway = null!;
    private HttpClient _owner = null!;
    private string _csrf = "";

    public async Task InitializeAsync()
    {
        _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ENACTIVE_REMOTE_DB", database.ConnectionString);
            builder.UseSetting("ENACTIVE_OWNER_KEY", OwnerKey);
            builder.UseSetting("ENACTIVE_DATA", Path.Combine(Path.GetTempPath(), database.Name));
            // Development: the cookie is not marked Secure, which an in-process test server cannot
            // satisfy. Everything else about the pipeline is what production runs.
            builder.UseSetting("environment", "Development");
        });

        _owner = _gateway.CreateClient();

        _csrf = (await Get<SessionView>("/api/session")).CsrfToken;
        await Post("/api/login", new { key = OwnerKey });
        _csrf = (await Get<SessionView>("/api/session")).CsrfToken;
    }

    public Task DisposeAsync()
    {
        _owner.Dispose();
        return _gateway.DisposeAsync().AsTask();
    }

    // ── the whole loop ──────────────────────────────────────────────────────

    /// <summary>
    /// Register a computer, connect it, publish a workspace, write a task, start it, let it run,
    /// and watch the ending arrive - through the real hub, the real authentication and the real
    /// database.
    /// </summary>
    [Fact]
    public async Task A_task_started_by_the_owner_runs_on_the_host_and_reports_back()
    {
        var device = await Post<DeviceView>("/api/hosts", new { name = "Studio PC" });

        await using var host = Connect(device.Token);
        await host.StartAsync();

        using var store = OpenStore();
        var loop = new DeliveryLoop(store, host);

        // Sync publishes the workspace. Until it has, the owner cannot name one - which is the
        // property that keeps a remote task from naming a folder.
        await loop.TurnAsync([new WorkspaceRef("workspace-1", "Enactive")]);

        var task = await Post<IdView>("/api/tasks", new
        {
            hostId = device.Id,
            workspaceId = "workspace-1",
            title = "Run the tests",
            prompt = "Please run them."
        });

        await Post($"/api/tasks/{task.Id}/start", new { commandId = Guid.NewGuid().ToString() });

        // The Host picks the command up, writes it down, acknowledges it and is handed the work.
        var accepted = Assert.Single(await loop.TurnAsync([new WorkspaceRef("workspace-1", "Enactive")]));
        var payload = RemoteJson.Deserialize<StartTaskPayload>(accepted.Payload);

        Assert.Equal("Run the tests", payload.Title);

        // Standing in for the engine: what stage 4 wires to this is already proven, and what is
        // under test here is that these events survive the wire.
        store.BeginRun(accepted.Id, payload.RunId);
        store.Enqueue(payload.RunId, RemoteEventKind.Running, "Started");
        store.Enqueue(payload.RunId, RemoteEventKind.Progress, "[1/1] Working");
        store.Enqueue(payload.RunId, RemoteEventKind.Completed, "All done");

        await loop.FlushAsync();

        Assert.Empty(store.NextOwed());

        // Deserialised into the gateway's OWN projection type, not a trimmed copy of it. The wire
        // format refuses fields the reader does not know, so a partial view fails here - which is
        // the setting doing its job, and a good reason to assert against the real contract.
        var state = await Get<GatewaySnapshot>("/api/state");

        // About THIS test's run and THIS test's computer, not about how many the database holds.
        // The first version asserted a single host and a single run: it passed alone and failed in
        // company, because the class shares one database and its neighbours register their own.
        var run = Assert.Single(state.Runs, r => r.Id == payload.RunId);

        Assert.Equal(RemoteRunStatus.Completed, run.Status);
        Assert.Equal("All done", run.Summary);
        Assert.True(Assert.Single(state.Hosts, h => h.Id == device.Id).Online);
    }

    /// <summary>
    /// A refusal has to arrive as a CODE, not as a sentence. It is the only thing that tells a Host
    /// whether to keep an event or throw it away, and if it does not survive the wire, every
    /// refusal becomes a retry, for ever.
    ///
    /// <para>This test is why the code is a RETURN VALUE. Its first version put it in a hub error's
    /// message, on the reasoning that a message is all an exception carries - and the message never
    /// arrives: SignalR puts its own text in front of it, and outside Development it does not send
    /// the exception's text at all. <c>HostReply&lt;T&gt;</c> came out of that.</para>
    /// </summary>
    [Fact]
    public async Task A_refusal_arrives_as_a_code_the_host_can_act_on()
    {
        var device = await Post<DeviceView>("/api/hosts", new { name = "Laptop" });

        await using var host = Connect(device.Token);
        await host.StartAsync();
        await host.SyncAsync([new WorkspaceRef("workspace-1", "Enactive")], CancellationToken.None);

        var refused = await Assert.ThrowsAsync<GatewayRefusedException>(() =>
            host.PublishAsync(
                new HostEvent("event-1", "a-run-that-does-not-exist", 1, RemoteEventKind.Progress),
                CancellationToken.None));

        Assert.Equal(FaultCode.UnknownRun, refused.Code);
        Assert.Equal(FaultDisposition.Drop, refused.Disposition);
    }

    /// <summary>
    /// Revocation bites on the connection that is already open, which is the case it exists for.
    ///
    /// <para>The open connection is ABORTED rather than answered - so the Host's next call fails as
    /// a transport error, not as a coded refusal. That surprised the first version of this test,
    /// which asserted a Fatal code and was wrong: a connection that has been cut cannot deliver an
    /// answer of any kind. What the code is for is the second half, below.</para>
    ///
    /// <para>The Host then does what it does with any transport failure - reconnects - and THAT is
    /// refused, at authentication, before a hub method runs. A withdrawn credential stops working
    /// in both directions.</para>
    /// </summary>
    [Fact]
    public async Task A_revoked_device_is_cut_off_and_cannot_get_back_in()
    {
        var device = await Post<DeviceView>("/api/hosts", new { name = "Old laptop" });

        await using var host = Connect(device.Token);
        await host.StartAsync();
        await host.SyncAsync([], CancellationToken.None);

        await Post($"/api/hosts/{device.Id}/revoke", new { });

        // The call on the cut connection fails, and not as a refusal - there was nobody left to
        // refuse it.
        await Assert.ThrowsAnyAsync<Exception>(() => host.SyncAsync([], CancellationToken.None));

        await using var reconnecting = Connect(device.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => reconnecting.StartAsync());
    }

    /// <summary>
    /// The credential goes in a header. A connection carrying none is refused before any hub method
    /// runs, which is what makes "the identity decides the HostId" true rather than aspirational.
    /// </summary>
    [Fact]
    public async Task A_connection_without_a_credential_is_refused()
    {
        await using var anonymous = Connect("not-a-real-token");

        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>A Host client pointed at the in-process gateway.</summary>
    private SignalRGatewayConnection Connect(string token)
        => new(new Uri(_gateway.Server.BaseAddress, "hubs/host"), token, options =>
        {
            options.HttpMessageHandlerFactory = _ => _gateway.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
        });

    private HostStore OpenStore()
        => new(Path.Combine(Path.GetTempPath(), "enactive-e2e-" + Guid.NewGuid().ToString("N"), "remote.db"));

    private async Task<T> Get<T>(string path)
        => (await _owner.GetFromJsonAsync<T>(path, RemoteJson.Options))!;

    private async Task Post(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: RemoteJson.Options)
        };
        request.Headers.Add("X-CSRF-TOKEN", _csrf);

        using var response = await _owner.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> Post<T>(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: RemoteJson.Options)
        };
        request.Headers.Add("X-CSRF-TOKEN", _csrf);

        using var response = await _owner.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options))!;
    }

    private sealed record SessionView(bool Authenticated, string CsrfToken);

    private sealed record DeviceView(string Id, string Name, string Token);

    private sealed record IdView(string Id);
}
