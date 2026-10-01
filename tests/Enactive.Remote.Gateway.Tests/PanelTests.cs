namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>
/// What the panel is served and what it is refused. Stage 6c of the remote-access design.
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

    // Task 3.8 rewrites this: the computer is registered to the person the test signs in as. Until
    // then these tests seed the old schema and fail at runtime anyway; this only keeps them compiling.
    private static readonly HostAccess Computer = new(HostId, "");

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

        await new HostService(new Database(database.ConnectionString)).PublishAsync(Computer, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 2, RemoteEventKind.ApprovalRequested, "Run the tests",
            new ApprovalRequest(approvalId, "call-1", "hash-1", RemoteDecidable: false, "e1:sealed-action")));

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

    /// <summary>
    /// Everything the page names is addressed by its own contents.
    ///
    /// <para>The panel's files were served with <c>Cache-Control: no-cache</c>, which asks a client
    /// to revalidate and then trusts it. A tab restored from iOS Safari's back-forward cache never
    /// asks: it is the page as it was, scripts included. So a release reached the server, the
    /// desktop showed it, and the same URL on a phone drew the previous build - which looks exactly
    /// like a release that failed, and is the second afternoon this has cost.</para>
    ///
    /// <para>This asserts the property that makes trust unnecessary: the token in the page is the
    /// fingerprint of the bytes actually served at that path. Not merely THAT there is a token -
    /// a constant one, or one left over from a previous build, would satisfy that and fix nothing.
    /// Change a file and its URL changes with it, and no cache anywhere holds an answer for the new
    /// one.</para>
    ///
    /// <para>Shown red by serving index.html unrewritten, which is what the static file handler
    /// does on its own.</para>
    /// </summary>
    [Fact]
    public async Task Every_script_and_stylesheet_the_page_names_is_fingerprinted()
    {
        var page = await _owner.GetStringAsync("/");

        var referenced = Regex.Matches(page, "(?:href|src)=\"(?<path>/[^\"?#]+\\.(?:css|js))(?<query>[^\"]*)\"");

        Assert.NotEmpty(referenced);

        foreach (Match reference in referenced)
        {
            var path = reference.Groups["path"].Value;
            var query = reference.Groups["query"].Value;

            Assert.StartsWith("?v=", query);

            // The token has to BE the file. A page that stamps something constant onto every asset
            // passes a "there is a version" check and goes on serving the same URL for a changed
            // file, which is the bug wearing the shape of the fix.
            var bytes = await _owner.GetByteArrayAsync(path + query);
            var expected = Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];

            Assert.Equal("?v=" + expected, query);
        }
    }

    /// <summary>
    /// And the page that names them is never the cached one. It is the pointer: every fingerprint
    /// is only as fresh as the document carrying it, so this is the one file that must still be
    /// asked about on every load.
    /// </summary>
    [Fact]
    public async Task The_page_itself_is_always_revalidated()
    {
        using var response = await _owner.GetAsync("/");

        Assert.Equal("no-cache", Assert.Single(response.Headers.CacheControl!.ToString().Split(", ")));
    }

    /// <summary>
    /// A permission says which run it belongs to.
    ///
    /// <para>Read on its own that is a dull field. It is what lets the question be drawn on the
    /// card of the task that raised it, instead of only in a list of its own - and that difference
    /// is the whole reason the panel stopped sending people to another tab to find out what they
    /// were needed for. Without it the panel can show that SOMETHING is being asked and not which
    /// task is asking, which is the state this started from.</para>
    ///
    /// <para>Shown red by projecting the approval with an empty RunId - it compiles, the page keeps
    /// rendering, and every question quietly stops belonging to anything.</para>
    /// </summary>
    [Fact]
    public async Task A_permission_names_the_run_that_raised_it()
    {
        var runId = await RunningRunAsync();
        var approvalId = "approval-" + Guid.NewGuid().ToString("N");

        await new HostService(new Database(database.ConnectionString)).PublishAsync(Computer, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 2, RemoteEventKind.ApprovalRequested, "Delete a file",
            new ApprovalRequest(approvalId, "call-1", "hash-1", RemoteDecidable: true, "e1:sealed-action")));

        var state = await _owner.GetFromJsonAsync<GatewaySnapshot>("/api/state", RemoteJson.Options);

        // By id, not Assert.Single: the tests in this class share one database, so other pending
        // approvals are legitimately in the snapshot alongside this one.
        var approval = Assert.Single(state!.Approvals, one => one.Id == approvalId);

        Assert.Equal(runId, approval.RunId);
    }

    /// <summary>
    /// An answered permission keeps reaching the panel, and says it was answered.
    ///
    /// <para>An answer is not an outcome. The gateway records DecisionQueued because the command
    /// still has to reach the computer and may be refused there - the desktop may have answered
    /// first, or the run may be over. That distinction was recorded, projected on every poll, and
    /// drawn nowhere: the card came back saying "Waiting" with both buttons live, exactly as it had
    /// looked before the click, so the only honest reading of the screen was that nothing had
    /// happened. Somebody pressing again would send Deny after Allow, which is a different command
    /// and not a retry.</para>
    ///
    /// <para>Two claims, and the card needs both: the answered approval is still in the snapshot at
    /// all - the projection could easily have filtered to Pending and dropped it - and its status
    /// arrives as the name the page compares against.</para>
    /// </summary>
    [Fact]
    public async Task An_answered_permission_still_reaches_the_panel_and_says_it_was_answered()
    {
        var runId = await RunningRunAsync();
        var approvalId = "approval-" + Guid.NewGuid().ToString("N");

        await new HostService(new Database(database.ConnectionString)).PublishAsync(Computer, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 2, RemoteEventKind.ApprovalRequested, "Delete a file",
            new ApprovalRequest(approvalId, "call-1", "hash-1", RemoteDecidable: true, "e1:sealed-action")));

        using var answer = new HttpRequestMessage(HttpMethod.Post, $"/api/approvals/{approvalId}/resolve")
        {
            Content = JsonContent.Create(
                new { commandId = Guid.NewGuid().ToString(), decision = "Allow", actionHash = "hash-1" },
                options: RemoteJson.Options)
        };
        answer.Headers.Add("X-CSRF-TOKEN", _csrf);
        (await _owner.SendAsync(answer)).EnsureSuccessStatusCode();

        var json = await _owner.GetStringAsync("/api/state");

        Assert.Contains(approvalId, json);
        Assert.Contains("\"status\":\"DecisionQueued\"", json);
    }

    /// <summary>
    /// The request itself reaches the panel, whole, and keeps reaching it.
    ///
    /// <para>The prompt is what the computer was actually told to do; the title is a heading its
    /// owner wrote. For a while the panel carried the prompt in every snapshot and rendered it
    /// nowhere, so a run could be read back only against its heading - and judging what a run did
    /// against a heading is judging it against the wrong thing.</para>
    ///
    /// <para>The DELTA poll is the half worth pinning. Tasks are a bounded, MUTABLE set and are
    /// sent whole on every poll; events are append-only and are sent as a delta. Moving tasks into
    /// the delta would look like an optimisation and would empty the panel's task map on the second
    /// poll - every run losing its title and its request three seconds after the page loaded.</para>
    /// </summary>
    [Fact]
    public async Task The_request_reaches_the_panel_verbatim_and_survives_a_delta_poll()
    {
        // Multi-line and quoted, because a prompt is prose somebody typed and the failure being
        // guarded against is truncation at the first line or the first quote.
        const string prompt = "Read README.md\nand save it as \"README.html\".\n\nKeep the headings.";

        var runId = await QueuedRunAsync(prompt);

        var first = await Get<GatewaySnapshot>("/api/state");
        var task = Assert.Single(first.Tasks, t => t.Id == TaskIdOf(first, runId));

        // Task 3.8 rewrites this test: the task arrives sealed, and only the panel can open it.
        Assert.Equal(prompt, task.Sealed);

        var second = await Get<GatewaySnapshot>($"/api/state?since={first.Cursor}");

        Assert.True(second.Delta);
        Assert.Equal(prompt, Assert.Single(second.Tasks, t => t.Id == task.Id).Sealed);
    }

    /// <summary>
    /// The panel is told to revalidate.
    ///
    /// <para>Nothing sent a Cache-Control header, so Cloudflare filled one in - four hours - and the
    /// panel's files are not fingerprinted. A release reached the server in ten minutes and the
    /// owner went on seeing the old page all afternoon, with no way to tell a stale page from a
    /// broken one. It had already cost an afternoon once, when a blank page was read as a
    /// deployment fault and was a cached one.</para>
    ///
    /// <para>Asserted at the ORIGIN, which is the half this repository controls. Whether the edge
    /// honours it is a Cloudflare setting, written down in REMOTE_OPERATIONS.md instead: a test
    /// here cannot see it, and one that pretended to would be worse than none.</para>
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    public async Task The_panel_is_never_served_as_fresh_for_hours(string path)
    {
        using var response = await _owner.GetAsync(path);

        response.EnsureSuccessStatusCode();

        var cache = response.Headers.CacheControl;

        Assert.NotNull(cache);
        Assert.True(cache!.NoCache, $"{path} was served as '{cache}', which a browser may reuse without asking.");
    }

    private static string TaskIdOf(GatewaySnapshot snapshot, string runId)
        => Assert.Single(snapshot.Runs, r => r.Id == runId).TaskId;

    // ── plumbing ────────────────────────────────────────────────────────────

    private async Task<string> RunningRunAsync()
    {
        var runId = await QueuedRunAsync();

        await new HostService(new Database(database.ConnectionString)).PublishAsync(Computer, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 1, RemoteEventKind.Running, "Started"));

        return runId;
    }

    private async Task<string> QueuedRunAsync(string? prompt = null)
    {
        await database.ExecuteAsync($"""
            INSERT IGNORE INTO hosts (id, name, token_hash, revoked, created_at)
            VALUES ('{HostId}', 'Host', SHA2('{HostId}', 256), 0, UTC_TIMESTAMP(3))
            """);

        var taskId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");

        // The prompt goes in as a PARAMETER while everything around it is interpolated. The rest of
        // these values are ids this method just generated; a prompt is prose from a test, and one
        // containing a quote or a backslash would otherwise fail as a syntax error somebody would
        // spend an afternoon reading as a projection bug.
        await database.ExecuteAsync($"""
            INSERT INTO tasks (id, host_id, workspace_id, title, prompt, created_at)
              VALUES ('{taskId}', '{HostId}', 'workspace-1', 'Test', @prompt, UTC_TIMESTAMP(3));
            INSERT INTO runs (id, task_id, host_id, status, created_at)
              VALUES ('{runId}', '{taskId}', '{HostId}', 'Queued', UTC_TIMESTAMP(3));
            """,
            ("@prompt", prompt ?? "Do it."));

        return runId;
    }

    private async Task<SessionView> Session()
        => (await _owner.GetFromJsonAsync<SessionView>("/api/session", RemoteJson.Options))!;

    private async Task<T> Get<T>(string path)
        => (await _owner.GetFromJsonAsync<T>(path, RemoteJson.Options))!;

    private sealed record SessionView(bool Authenticated, string CsrfToken);
}
