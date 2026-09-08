namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>
/// What the panel is served and what it is refused. Stage 6c of <c>Docs/REMOTE_DESIGN.md</c>.
///
/// <para>The panel itself is JavaScript and is not tested here. What is tested is everything it
/// depends on that a C# change can break without anyone noticing: the shape of the JSON, the
/// binding of the cursor, what is reachable without signing in, and the refusal that a missing
/// button must not be the only thing enforcing.</para>
/// </summary>
public sealed class PanelTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string OwnerKey = "a-development-owner-key-for-tests";
    private const string HostId = "4444444444444444444444444444dddd";

    private WebApplicationFactory<Program> _gateway = null!;
    private HttpClient _owner = null!;
    private HttpClient _stranger = null!;
    private string _csrf = "";

    public async Task InitializeAsync()
    {
        _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ENACTIVE_REMOTE_DB", database.ConnectionString);
            builder.UseSetting("ENACTIVE_OWNER_KEY", OwnerKey);
            builder.UseSetting("ENACTIVE_DATA", Path.Combine(Path.GetTempPath(), database.Name));
            builder.UseSetting("environment", "Development");
        });

        _stranger = _gateway.CreateClient();
        _owner = _gateway.CreateClient();

        _csrf = (await Session()).CsrfToken;

        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/login")
        {
            Content = JsonContent.Create(new { key = OwnerKey }, options: RemoteJson.Options)
        };
        login.Headers.Add("X-CSRF-TOKEN", _csrf);
        (await _owner.SendAsync(login)).EnsureSuccessStatusCode();

        _csrf = (await Session()).CsrfToken;
    }

    public Task DisposeAsync()
    {
        _owner.Dispose();
        _stranger.Dispose();
        return _gateway.DisposeAsync().AsTask();
    }

    /// <summary>
    /// An enum reaches the browser as its NAME.
    ///
    /// <para>ASP.NET's default writes it as a number, and every test that reads the reply back
    /// through a typed client passes either way, because the reader accepts both. So this one reads
    /// the raw text. A number would mean that inserting a member into the middle of
    /// <see cref="RemoteRunStatus"/> silently renumbers every status the panel was taught, and the
    /// panel would go on rendering - confidently, and wrong.</para>
    ///
    /// <para>Shown red by removing the JSON configuration in <c>Program</c>: the reply then carries
    /// <c>"status":0</c>.</para>
    /// </summary>
    [Fact]
    public async Task An_enum_reaches_the_panel_as_a_name_and_not_a_number()
    {
        await QueuedRunAsync();

        var json = await _owner.GetStringAsync("/api/state");

        Assert.Contains("\"status\":\"Queued\"", json);
        Assert.DoesNotContain("\"status\":0", json);
    }

    /// <summary>
    /// The page is public and the state behind it is not. The panel has to render its own sign-in
    /// form, so the HTML cannot be behind the cookie that the form exists to obtain.
    /// </summary>
    [Fact]
    public async Task The_page_is_served_to_anyone_and_the_state_behind_it_to_nobody()
    {
        using var page = await _stranger.GetAsync("/");
        using var state = await _stranger.GetAsync("/api/state");

        page.EnsureSuccessStatusCode();
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, state.StatusCode);
    }

    /// <summary>
    /// The cursor survives the query string. It is a <c>long?</c> bound from <c>?since=</c>, and a
    /// binding that quietly failed would send a full snapshot every three seconds while every test
    /// that calls the projection directly stayed green.
    /// </summary>
    [Fact]
    public async Task A_poll_that_carries_a_cursor_is_answered_as_a_delta()
    {
        await QueuedRunAsync();

        var first = await Get<GatewaySnapshot>("/api/state");
        var second = await Get<GatewaySnapshot>($"/api/state?since={first.Cursor}");

        Assert.False(first.Delta);
        Assert.True(second.Delta);
        Assert.Empty(second.Events);
    }

    /// <summary>
    /// A shell is refused over HTTP, by the server, whatever the page drew.
    ///
    /// <para>The panel renders a shell request as an explanation instead of a button. That is a
    /// courtesy, not a boundary: a hidden button is not a guard, and anyone can post to this URL.
    /// The refusal has to be here, and it has to arrive with a code the page can turn into the
    /// right sentence.</para>
    /// </summary>
    [Fact]
    public async Task A_shell_permission_is_refused_over_http_and_not_merely_undrawn()
    {
        var runId = await RunningRunAsync();
        var approvalId = "approval-" + Guid.NewGuid().ToString("N");

        await new HostService(new Database(database.ConnectionString)).PublishAsync(HostId, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 2, RemoteEventKind.ApprovalRequested, "Run the tests",
            new ApprovalRequest(approvalId, "call-1", "run_command", "dotnet test", "C:/work",
                "hash-1", RemoteDecidable: false)));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/approvals/{approvalId}/resolve")
        {
            Content = JsonContent.Create(
                // As a NAME, which is how the panel will send it and which the gateway could not
                // read at all until this stage configured its JSON.
                new { commandId = Guid.NewGuid().ToString(), decision = "Allow", actionHash = "hash-1" },
                options: RemoteJson.Options)
        };
        request.Headers.Add("X-CSRF-TOKEN", _csrf);

        using var response = await _owner.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(FaultCode.ApprovalNotRemotelyDecidable, body);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private async Task<string> RunningRunAsync()
    {
        var runId = await QueuedRunAsync();

        await new HostService(new Database(database.ConnectionString)).PublishAsync(HostId, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 1, RemoteEventKind.Running, "Started"));

        return runId;
    }

    private async Task<string> QueuedRunAsync()
    {
        await database.ExecuteAsync($"""
            INSERT IGNORE INTO hosts (id, name, token_hash, revoked, created_at)
            VALUES ('{HostId}', 'Host', SHA2('{HostId}', 256), 0, UTC_TIMESTAMP(3))
            """);

        var taskId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");

        await database.ExecuteAsync($"""
            INSERT INTO tasks (id, host_id, workspace_id, title, prompt, created_at)
              VALUES ('{taskId}', '{HostId}', 'workspace-1', 'Test', 'Do it.', UTC_TIMESTAMP(3));
            INSERT INTO runs (id, task_id, host_id, status, created_at)
              VALUES ('{runId}', '{taskId}', '{HostId}', 'Queued', UTC_TIMESTAMP(3));
            """);

        return runId;
    }

    private async Task<SessionView> Session()
        => (await _owner.GetFromJsonAsync<SessionView>("/api/session", RemoteJson.Options))!;

    private async Task<T> Get<T>(string path)
        => (await _owner.GetFromJsonAsync<T>(path, RemoteJson.Options))!;

    private sealed record SessionView(bool Authenticated, string CsrfToken);
}
