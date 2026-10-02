namespace Enactive.Remote.Gateway.Tests;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Host;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The two halves, talking to each other. Stage 6a of the remote-access design.
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
    private WebApplicationFactory<Program> _gateway = null!;
    private PanelClient _owner = null!;

    public async Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);
        _owner = await PanelClient.SignedInAsync(_gateway, "owner-" + Guid.NewGuid().ToString("N")[..8]);
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
    ///
    /// <para>And sealed end to end. The browser seals the task and the start under the computer's
    /// key; the gateway carries both without being able to read either; the computer opens them,
    /// checks they agree, and hands the engine the prompt the person wrote. Its ending comes back
    /// sealed the other way, and only the browser opens it. A gateway that rewrote, dropped or
    /// re-addressed any of it would fail here, at the end that holds the key.</para>
    /// </summary>
    [Fact]
    public async Task A_task_started_by_the_owner_runs_on_the_host_and_reports_back()
    {
        var device = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Studio PC" });
        var browser = new TestBrowser(device.Id);
        var sealer = browser.Computer.Sealer();

        await using var host = Connect(device.Token);
        await host.StartAsync();

        using var store = OpenStore();
        var loop = new DeliveryLoop(store, host, sealer);
        IReadOnlyList<WorkspaceRef> workspaces =
            [new WorkspaceRef("workspace-1", sealer.WorkspaceName("workspace-1", "Enactive"))];

        // Sync publishes the workspace. Until it has, the owner cannot name one - which is the
        // property that keeps a remote task from naming a folder.
        await loop.TurnAsync(workspaces);

        var taskId = Guid.NewGuid().ToString();
        await _owner.PostAsync<IdView>("/api/tasks", new
        {
            taskId,
            hostId = device.Id,
            workspaceId = "workspace-1",
            sealedTask = browser.Task(taskId, "workspace-1", "Run the tests", "Please run them.")
        });

        var commandId = Guid.NewGuid().ToString();
        await _owner.PostAsync($"/api/tasks/{taskId}/start", new
        {
            commandId,
            @sealed = browser.Start(commandId, taskId, "workspace-1")
        });

        // The Host picks the command up, writes it down, acknowledges it and is handed the work.
        var accepted = Assert.Single(await loop.TurnAsync(workspaces));
        var runId = RemoteJson.Deserialize<StartTaskPayload>(accepted.Payload).RunId;

        // A stand-in engine: what a real run does is what the engine's own tests are for. What is
        // under test here is that the prompt it is handed is the one the person sealed, and that its
        // ending survives the wire.
        var engine = new ScriptedEngine(
            Event(EventKind.StepStarted, "[1/1] Working"),
            Event(EventKind.TaskCompleted, "All done", WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)));
        var runner = new RemoteRunner(store, new RemoteApprovals(), sealer, (task, _, _) =>
            Task.FromResult(new RemotePreparation(engine, new Intent(
                Guid.NewGuid(), task.Prompt, IntentSource.Remote,
                new WorkContext(Guid.NewGuid(), task.WorkspaceId, null, null, null, [], []),
                DateTimeOffset.UtcNow))));

        await runner.ApplyAsync(accepted);

        Assert.Equal("Please run them.", engine.Prompt);

        await loop.FlushAsync();

        Assert.Empty(store.NextOwed());

        // Deserialised into the gateway's OWN projection type, not a trimmed copy of it. The wire
        // format refuses fields the reader does not know, so a partial view fails here - which is
        // the setting doing its job, and a good reason to assert against the real contract.
        var state = await _owner.GetAsync<GatewaySnapshot>("/api/state");

        // About THIS test's run and THIS test's computer, not about how many the database holds.
        // The first version asserted a single host and a single run: it passed alone and failed in
        // company, because the class shares one database and its neighbours register their own.
        var run = Assert.Single(state.Runs, r => r.Id == runId);

        Assert.Equal(RemoteRunStatus.Completed, run.Status);
        Assert.Equal("All done", browser.OpenSummary(run, RemoteEventKind.Completed));
        Assert.Equal("Run the tests", browser.OpenTask(Assert.Single(state.Tasks, t => t.Id == taskId)).Title);
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
        var device = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Laptop" });
        var sealer = new TestBrowser(device.Id).Computer.Sealer();

        await using var host = Connect(device.Token);
        await host.StartAsync();
        await host.SyncAsync(
            [new WorkspaceRef("workspace-1", sealer.WorkspaceName("workspace-1", "Enactive"))],
            CancellationToken.None);

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
        var device = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Old laptop" });

        await using var host = Connect(device.Token);
        await host.StartAsync();
        await host.SyncAsync([], CancellationToken.None);

        await _owner.PostAsync($"/api/hosts/{device.Id}/revoke", new { });

        // The call on the cut connection fails, and not as a refusal - there was nobody left to
        // refuse it.
        await Assert.ThrowsAnyAsync<Exception>(() => host.SyncAsync([], CancellationToken.None));

        await using var reconnecting = Connect(device.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => reconnecting.StartAsync());
    }

    /// <summary>
    /// A computer's calls are limited per computer. Past the limit a call is answered with a code the
    /// Host keeps the item for and sends again, not with an exception it could not classify; the
    /// person's other computer is not slowed by the busy one; and the busy one is slowed, not shut out -
    /// a moment later its retry goes through, where a minute's window would refuse it to the minute's
    /// end and the Host would park its events.
    /// </summary>
    [Fact]
    public async Task A_computer_calling_too_often_is_told_to_retry_and_another_is_not()
    {
        var busy = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Busy" });
        var quiet = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Quiet" });

        await using var busyHub = RawConnect(busy.Token);
        await using var quietHub = RawConnect(quiet.Token);
        await busyHub.StartAsync();
        await quietHub.StartAsync();

        // The bucket refills while the calls are made, so it takes a few more than its size to empty.
        var answered = 0;
        RemoteFault? refused = null;
        while (refused is null && answered < 2 * RequestLimits.HubCallsPerMinute)
        {
            refused = (await HelloAsync(busyHub)).Fault;
            answered += refused is null ? 1 : 0;
        }

        var other = await HelloAsync(quietHub);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var retried = await HelloAsync(busyHub);

        Assert.True(answered >= RequestLimits.HubCallsPerMinute, $"Refused after {answered} calls.");
        Assert.Equal(FaultCode.QuotaExceeded, refused?.Code);
        Assert.Equal(FaultDisposition.Retry, RemoteFaults.DispositionOf(refused?.Code));
        Assert.Null(other.Fault);
        Assert.True(other.Value);
        Assert.Null(retried.Fault);
    }

    /// <summary>
    /// The credential goes in a header. A connection carrying none is refused before any hub method
    /// runs, which is what makes "the identity decides the HostId" true rather than aspirational.
    ///
    /// <para>And the Host says so in a sentence. The refusal arrives as a bare 401, which the service
    /// used to treat as a dropped connection and dial again for ever, with nothing saying why.</para>
    /// </summary>
    [Fact]
    public async Task A_connection_without_a_credential_is_refused()
    {
        await using var anonymous = Connect("not-a-real-token");

        var refused = await Assert.ThrowsAsync<GatewayCredentialRefusedException>(() => anonymous.StartAsync());

        Assert.Equal(GatewayCredentialRefusedException.Sentence, refused.Message);
    }

    // ── connecting with a code ──────────────────────────────────────────────

    /// <summary>
    /// Spec §5.2 through the real gateway: the browser registers a computer, shows a connection code,
    /// and the computer that applies it says hello and publishes a grant that browser - and only a
    /// holder of the code's pairing secret - can verify. Opened here as the browser opens it: with the
    /// code's pair key, nothing pinned yet, and the signing key it carries being the one to pin.
    ///
    /// <para>The key store is the real one, on a file of its own, because the grant is made from what
    /// it holds: a fixed key standing in for it would prove the grant format and not that the keys the
    /// computer keeps are the keys it grants.</para>
    /// </summary>
    [Fact]
    public async Task A_connection_code_pairs_the_device_it_names()
    {
        // The browser: its device key, registered, then a computer registered and a code made.
        using var browserKey = P256.Generate();
        var devicePublic = P256.PublicRaw(browserKey);
        var deviceId = (await _owner.PostAsync<IdView>("/api/devices", new { publicKey = B64.Url(devicePublic), label = "Laptop" })).Id;
        var computer = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Studio PC" });
        var text = new ConnectionCode(_gateway.Server.BaseAddress, computer.Id, computer.Token, deviceId, devicePublic,
            RandomNumberGenerator.GetBytes(32)).Format();

        // The computer: the code as the person pasted it, applied to its own key store.
        var code = Assert.IsType<ConnectionCode>(Pairing.TryRead(text, out var problem), exactMatch: true);
        Assert.Equal(string.Empty, problem);
        using var store = OpenStore();
        using var keys = new HostKeyStore(store, code.HostId);
        var pairKey = code.PairKey;
        Pairing.Apply(code, keys);

        await using var host = Connect(code.Token);
        await host.StartAsync();
        await host.HelloAsync(RemoteProtocol.Version, CancellationToken.None);
        await new DeliveryLoop(store, host, new Sealer(keys, TimeProvider.System), keys).TurnAsync([]);

        Assert.Empty(keys.PendingGrants());

        // The browser again: its grants, read with its device header, and opened.
        using var response = await _owner.SendAsync(HttpMethod.Get, "/api/grants",
            configure: request => request.Headers.Add(DeviceHeader.Name, deviceId));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var held = await response.Content.ReadFromJsonAsync<List<HostGrantsView>>(RemoteJson.Options);
        var forComputer = Assert.Single(held!, g => g.HostId == computer.Id);
        var grant = Assert.Single(forComputer.Grants);

        var (key, signing) = Grants.Open(grant, browserKey, pairKey, pinnedHostSigningPublic: null);

        Assert.Equal(1u, forComputer.KeyEpoch);
        Assert.Equal(keys.Current.Secret.ToArray(), key.Secret.ToArray());
        Assert.Equal(keys.SigningPublic, signing);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>A Host client pointed at the in-process gateway.</summary>
    private SignalRGatewayConnection Connect(string token)
        => new(new Uri(_gateway.Server.BaseAddress, "hubs/host"), token, options =>
        {
            options.HttpMessageHandlerFactory = _ => _gateway.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
        });

    /// <summary>
    /// A bare hub connection, for calls the Host's client has no method for, such as <c>Hello</c>, and
    /// whose whole reply a test wants to see. Serialised as both ends serialise.
    /// </summary>
    private HubConnection RawConnect(string token)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(_gateway.Server.BaseAddress, "hubs/host"), options =>
            {
                options.Headers["Authorization"] = $"Bearer {token}";
                options.HttpMessageHandlerFactory = _ => _gateway.Server.CreateHandler();
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

    private static Task<HostReply<bool>> HelloAsync(HubConnection hub)
        => hub.InvokeAsync<HostReply<bool>>("Hello", RemoteProtocol.Version);

    private static HostStore OpenStore()
        => new(Path.Combine(Path.GetTempPath(), "enactive-e2e-" + Guid.NewGuid().ToString("N"), "remote.db"));

    private static WorkEvent Event(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, kind, summary, payload);

    private sealed record DeviceView(string Id, string Name, string Token);

    private sealed record IdView(string Id);

    /// <summary>What <c>GET /api/grants</c> answers for one computer. Every field, because the wire refuses unknown ones.</summary>
    private sealed record HostGrantsView(string HostId, uint KeyEpoch, List<KeyGrant> Grants);

    /// <summary>An engine that emits what the test says, and remembers the prompt it was given.</summary>
    private sealed class ScriptedEngine(params WorkEvent[] events) : IOrchestrator
    {
        public string? Prompt { get; private set; }

        public async IAsyncEnumerable<WorkEvent> SubmitIntentAsync(
            Intent intent, [EnumeratorCancellation] CancellationToken ct)
        {
            Prompt = intent.RawText;

            foreach (var published in events)
            {
                await Task.Yield();
                yield return published;
            }
        }

        public IAsyncEnumerable<WorkEvent> ResumeRunAsync(
            RunCheckpoint checkpoint, WorkContext context, CancellationToken ct)
            => throw new NotSupportedException("Resuming is not part of what this test exercises.");
    }
}
