namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Enactive.Remote.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
    private const string Workspace = "workspace-1";

    private static readonly string ActionHash = Ids.Hash("run_command dotnet test");

    private WebApplicationFactory<Program> _gateway = null!;
    private PanelClient _owner = null!;
    private PanelClient _stranger = null!;

    private HostService Hosts => new(new Database(database.ConnectionString));

    // xUnit makes a new instance for each test, so each test signs in a person of its own: counts such
    // as the unread notices are then this test's alone, whatever its neighbours left in the database.
    public async Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);
        _stranger = new PanelClient(_gateway);
        _owner = await PanelClient.SignedInAsync(_gateway, "owner-" + Guid.NewGuid().ToString("N")[..8]);
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

        var json = await _owner.Http.GetStringAsync("/api/state");

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
        using var page = await _stranger.Http.GetAsync("/");
        using var state = await _stranger.Http.GetAsync("/api/state");

        page.EnsureSuccessStatusCode();
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, state.StatusCode);
    }

    /// <summary>
    /// An invitation link opens the panel. The link is <c>/pair#…</c>, and the page was served only for
    /// <c>/</c> and <c>/index.html</c>: the new device opening it met a 404, and the secret in its fragment
    /// was never read. The same page, fingerprinted and revalidated like the one at <c>/</c>.
    /// </summary>
    [Fact]
    public async Task The_pair_route_serves_the_panel_page()
    {
        using var pair = await _stranger.Http.GetAsync("/pair");
        var root = await _stranger.Http.GetStringAsync("/");

        Assert.Equal(HttpStatusCode.OK, pair.StatusCode);
        Assert.Equal("text/html", pair.Content.Headers.ContentType?.MediaType);
        Assert.True(pair.Headers.CacheControl?.NoCache);
        Assert.Equal(root, await pair.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The vendored QR generator is served as JavaScript. The panel imports it as a module, and a browser runs
    /// a module only when it comes with a JavaScript type: served as anything else, or not at all, the import
    /// fails and with it the whole panel, whose first line imports everything it uses.
    /// </summary>
    [Fact]
    public async Task The_vendored_qr_module_is_served_as_javascript()
    {
        using var module = await _stranger.Http.GetAsync("/vendor/qrcode.mjs");

        Assert.Equal(HttpStatusCode.OK, module.StatusCode);
        Assert.Equal("text/javascript", module.Content.Headers.ContentType?.MediaType);
        Assert.True(module.Headers.CacheControl?.NoCache);
    }

    /// <summary>
    /// The cursor survives the query string. It is a <c>string?</c> bound from <c>?since=</c>, and a
    /// binding that quietly failed would send a full snapshot every three seconds while every test
    /// that calls the projection directly stayed green.
    /// </summary>
    [Fact]
    public async Task A_poll_that_carries_a_cursor_is_answered_as_a_delta()
    {
        await QueuedRunAsync();

        var first = await _owner.GetAsync<GatewaySnapshot>("/api/state");
        var second = await _owner.GetAsync<GatewaySnapshot>($"/api/state?since={first.Cursor}");

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
        var (computer, runId) = await RunningRunAsync();
        var approvalId = await AskAsync(computer, runId, remoteDecidable: false);

        // The decision as a NAME, which is how the panel will send it and which the gateway could not
        // read at all until this stage configured its JSON.
        using var response = await _owner.SendAsync(
            HttpMethod.Post, $"/api/approvals/{approvalId}/resolve", Answer(computer, approvalId, "Allow"));
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
        var page = await _owner.Http.GetStringAsync("/");

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
            var bytes = await _owner.Http.GetByteArrayAsync(path + query);
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
        using var response = await _owner.Http.GetAsync("/");

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
        var (computer, runId) = await RunningRunAsync();
        var approvalId = await AskAsync(computer, runId, remoteDecidable: true);

        var state = await _owner.GetAsync<GatewaySnapshot>("/api/state");

        var approval = Assert.Single(state.Approvals, one => one.Id == approvalId);

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
        var (computer, runId) = await RunningRunAsync();
        var approvalId = await AskAsync(computer, runId, remoteDecidable: true);

        await _owner.PostAsync($"/api/approvals/{approvalId}/resolve", Answer(computer, approvalId, "Allow"));

        var json = await _owner.Http.GetStringAsync("/api/state");

        Assert.Contains(approvalId, json);
        Assert.Contains("\"status\":\"DecisionQueued\"", json);
    }

    /// <summary>
    /// The request itself reaches the panel, whole, and keeps reaching it.
    ///
    /// <para>Sealed: only a browser holding the computer's key can read it, so "verbatim" is about
    /// what opens. The gateway stores the envelope and passes it on, and a gateway that altered one
    /// byte of it would hand the panel something that does not open at all.</para>
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

        var (computer, runId) = await QueuedRunAsync(prompt);

        var first = await _owner.GetAsync<GatewaySnapshot>("/api/state");
        var task = Assert.Single(first.Tasks, t => t.Id == TaskIdOf(first, runId));

        Assert.DoesNotContain("README", task.Sealed, StringComparison.Ordinal);
        Assert.Equal(prompt, computer.Browser.OpenTask(task).Prompt);

        var second = await _owner.GetAsync<GatewaySnapshot>($"/api/state?since={first.Cursor}");

        Assert.True(second.Delta);
        Assert.Equal(prompt, computer.Browser.OpenTask(Assert.Single(second.Tasks, t => t.Id == task.Id)).Prompt);
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
        using var response = await _owner.Http.GetAsync(path);

        response.EnsureSuccessStatusCode();

        var cache = response.Headers.CacheControl;

        Assert.NotNull(cache);
        Assert.True(cache!.NoCache, $"{path} was served as '{cache}', which a browser may reuse without asking.");
    }

    // ── signing in ──────────────────────────────────────────────────────────

    /// <summary>
    /// The page offers exactly the sign-in methods the gateway has, because it has none of its own.
    ///
    /// <para>A button written into the page would be offered whatever the gateway was configured with:
    /// a "Continue with Google" on a gateway without Google is a link to a 404, and the person reads it
    /// as a broken sign-in rather than a missing one. So the page carries an empty place for the buttons
    /// and no <c>/auth/</c> link at all, and its script draws one per name in <c>/api/providers</c> -
    /// which lists only what is configured.</para>
    ///
    /// <para>The owner-key form is gone with it. Its password field asked for a key the gateway no
    /// longer has, and a sign-in that cannot succeed reads as a wrong key, typed again and again.</para>
    /// </summary>
    [Fact]
    public async Task The_page_offers_only_configured_providers()
    {
        await using var githubOnly = TestGateway.Create(database, configure: builder =>
        {
            builder.UseSetting(ExternalProviders.PublicOriginSetting, "https://remote.example.test");
            builder.UseSetting("ENACTIVE_GITHUB_CLIENT_ID", "github-client");
            builder.UseSetting("ENACTIVE_GITHUB_CLIENT_SECRET", "github-secret");
        });
        using var browser = new PanelClient(githubOnly);

        Assert.Equal(["github"], await browser.GetAsync<string[]>("/api/providers"));

        var page = await browser.Http.GetStringAsync("/");

        Assert.DoesNotContain("/auth/", page, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"password\"", page, StringComparison.Ordinal);
        Assert.Matches("<div id=\"providers\"[^>]*></div>", page);

        var script = Regex.Match(page, "src=\"(?<path>/app\\.js\\?v=[0-9a-f]+)\"").Groups["path"].Value;

        Assert.Contains("/api/providers", await browser.Http.GetStringAsync(script), StringComparison.Ordinal);

        // Nor any script the page can import. A link written into a module is the same hard-coded
        // button as one written into the page, only harder to find: the one sign-in path a script may
        // name is the template the listed provider is put into.
        var webRoot = githubOnly.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var scripts = Directory.GetFiles(Path.Combine(webRoot, "js"), "*.js")
            .Select(file => "/js/" + Path.GetFileName(file))
            .Prepend("/app.js")
            .ToList();

        Assert.Contains("/js/signin.js", scripts);

        foreach (var path in scripts)
        {
            var source = await browser.Http.GetStringAsync(path);

            Assert.DoesNotMatch("/auth/(?!\\$\\{)", source);
        }
    }

    /// <summary>
    /// Whether the development sign-in exists is answered without attempting one. The panel used to ask
    /// by posting an empty name, which the sign-in's limit counted: every reload of a signed-out page on
    /// localhost spent one of the address's twenty sign-ins a minute, shared with the providers' starts,
    /// and a browser test signing in repeatedly met 429s that had nothing to do with what it tested. A
    /// GET of the POST-only route is answered 405 where it exists and 404 where it does not, by routing,
    /// which no limit counts.
    /// </summary>
    [Fact]
    public async Task The_development_sign_in_is_discoverable_without_a_sign_in_attempt()
    {
        // More probes than the limit allows sign-ins: counted, the last of them would be a 429.
        for (var i = 0; i <= RequestLimits.AuthPerMinute; i++)
        {
            using var probe = await _stranger.Http.GetAsync("/api/dev/sign-in");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, probe.StatusCode);
        }

        // And the sign-ins themselves are all still there.
        using var late = await PanelClient.SignedInAsync(_gateway, "late-" + Guid.NewGuid().ToString("N")[..8]);

        await using var production = TestGateway.Create(database, devSignIn: false);
        using var stranger = new PanelClient(production);
        using var absent = await stranger.Http.GetAsync("/api/dev/sign-in");

        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    /// <summary>
    /// The state says whose it is.
    ///
    /// <para>The cookie belongs to the browser and not to the tab. Bob signing in, in another window,
    /// changes whose state Alice's open tab is polling for, and nothing in the answer said so: the tab
    /// went on folding Bob's runs and notices into Alice's screen, under Alice's name. With the account
    /// in every snapshot the panel compares it with the account it signed in as, and a mismatch resets
    /// the page instead of drawing anything.</para>
    /// </summary>
    [Fact]
    public async Task The_state_names_the_account_it_belongs_to()
    {
        using var other = await PanelClient.SignedInAsync(_gateway, "other-" + Guid.NewGuid().ToString("N")[..8]);

        var owners = await _owner.Http.GetStringAsync("/api/state");
        var others = await other.Http.GetStringAsync("/api/state");
        var delta = await _owner.GetAsync<GatewaySnapshot>("/api/state");
        var ownersDelta = await _owner.Http.GetStringAsync($"/api/state?since={delta.Cursor}");

        Assert.Contains($"\"userId\":\"{_owner.UserId}\"", owners, StringComparison.Ordinal);
        Assert.Contains($"\"userId\":\"{other.UserId}\"", others, StringComparison.Ordinal);
        Assert.Contains("\"delta\":true", ownersDelta, StringComparison.Ordinal);
        Assert.Contains($"\"userId\":\"{_owner.UserId}\"", ownersDelta, StringComparison.Ordinal);
        Assert.NotEqual(_owner.UserId, other.UserId);
    }

    /// <summary>
    /// The privacy notice and the terms are pages of their own, served to anyone, and the sign-in links
    /// to both: a person is asked to sign in with an account of another company's, and what is kept about
    /// them has to be readable before they do it, not after.
    /// </summary>
    [Theory]
    [InlineData("/privacy.html")]
    [InlineData("/terms.html")]
    public async Task Privacy_and_terms_are_served(string path)
    {
        using var response = await _stranger.Http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"href=\"{path}\"", await _stranger.Http.GetStringAsync("/"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both texts are drafts until the operator approves them, and say so at the top: they are legal
    /// documents, and a page that read as final before anyone had agreed to it would be promising what
    /// nobody had decided. Removing the banner is part of approving the text, and so is changing this
    /// test. Neither page runs a script - a page about what the service can see should not itself be
    /// code - and each leads back to the panel.
    /// </summary>
    [Theory]
    [InlineData("/privacy.html", "Enactive · Privacy")]
    [InlineData("/terms.html", "Enactive · Terms")]
    public async Task The_policy_pages_are_drafts_without_scripts(string path, string title)
    {
        var page = await _stranger.Http.GetStringAsync(path);

        Assert.Contains($"<title>{title}</title>", page, StringComparison.Ordinal);
        Assert.Contains("DRAFT — not yet approved by the operator", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The privacy notice points at the published list of the panel's files: it is where the notice tells
    /// a person how to check the one thing encryption in a web page cannot promise.
    /// </summary>
    [Fact]
    public async Task The_privacy_notice_points_at_the_panel_manifest()
    {
        var page = await _stranger.Http.GetStringAsync("/privacy.html");

        Assert.Contains("href=\"/.well-known/enactive-panel.json\"", page, StringComparison.Ordinal);
    }

    private static string TaskIdOf(GatewaySnapshot snapshot, string runId)
        => Assert.Single(snapshot.Runs, r => r.Id == runId).TaskId;

    // ── marking read, and device commands, over HTTP ────────────────────────

    /// <summary>
    /// "Mark all read" sends the cursor of the snapshot on the screen, exactly as the panel was given
    /// it, and marks what that snapshot showed. A cursor that is malformed, or of another epoch of the
    /// line, marks nothing: the first is not a position at all, and after a reset of the line the
    /// second counts notices no screen has shown.
    /// </summary>
    [Fact]
    public async Task Marking_read_takes_the_cursor_the_panel_was_given()
    {
        var (computer, runId) = await RunningRunAsync();
        await Hosts.PublishAsync(computer.Access, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 2, RemoteEventKind.Completed,
            computer.Sealer.Detail(runId, 2, RemoteEventKind.Completed, "Done")));

        var shown = await _owner.GetAsync<GatewaySnapshot>("/api/state");
        Assert.Equal(1, shown.UnreadNotices);

        var otherEpoch = "99" + shown.Cursor[shown.Cursor.IndexOf('.')..];

        foreach (var refused in new[] { "not-a-cursor", "-1.5", otherEpoch })
        {
            using var response = await _owner.SendAsync(HttpMethod.Post, "/api/notices/read", new { through = refused });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(1, (await _owner.GetAsync<GatewaySnapshot>("/api/state")).UnreadNotices);

        await _owner.PostAsync("/api/notices/read", new { through = shown.Cursor });

        Assert.Equal(0, (await _owner.GetAsync<GatewaySnapshot>("/api/state")).UnreadNotices);
    }

    /// <summary>
    /// A device command goes to the computer as the browser sealed it, and the computer opens it. Like
    /// every state-changing call it needs the antiforgery token, and it carries device kinds only: a
    /// start sent this way would skip every check a start is given.
    /// </summary>
    [Fact]
    public async Task A_device_command_reaches_the_computer_as_it_was_sealed()
    {
        var computer = await ComputerAsync();
        var path = $"/api/hosts/{computer.Access.HostId}/device-commands";

        var commandId = Guid.NewGuid().ToString();
        var command = new { commandId, kind = "RevokeDevice", @sealed = computer.Browser.Revocation(commandId, "device-1") };

        using (var forged = await _owner.SendAsync(HttpMethod.Post, path, command, csrf: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        var startId = Guid.NewGuid().ToString();
        using (var start = await _owner.SendAsync(HttpMethod.Post, path,
                   new { commandId = startId, kind = "StartTask", @sealed = computer.Browser.Revocation(startId, "device-1") }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        }

        await _owner.PostAsync(path, command);

        var delivered = Assert.Single(await Hosts.SyncAsync(computer.Access, computer.Workspaces), c => c.Id == commandId);

        Assert.Equal(CommandKind.RevokeDevice, delivered.Kind);
        Assert.Equal("device-1", computer.Sealer.OpenRevocation(delivered).DeviceId);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>
    /// A computer of the signed-in person's, sharing its key with a browser of theirs, that has
    /// published one workspace.
    /// </summary>
    private sealed record Computer(HostAccess Access, TestBrowser Browser)
    {
        public Sealer Sealer { get; } = Browser.Computer.Sealer();

        public IReadOnlyList<WorkspaceRef> Workspaces
            => [new WorkspaceRef(Workspace, Sealer.WorkspaceName(Workspace, "Enactive"))];
    }

    private async Task<Computer> ComputerAsync()
    {
        var device = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Studio PC" });
        var computer = new Computer(new HostAccess(device.Id, _owner.UserId), new TestBrowser(device.Id));

        await Hosts.SyncAsync(computer.Access, computer.Workspaces);
        return computer;
    }

    /// <summary>A task written and started through the panel's API, sealed as the panel seals it.</summary>
    private async Task<(Computer Computer, string RunId)> QueuedRunAsync(string prompt = "Do it.")
    {
        var computer = await ComputerAsync();
        var hostId = computer.Access.HostId;

        var taskId = Guid.NewGuid().ToString();
        await _owner.PostAsync("/api/tasks", new
        {
            taskId,
            hostId,
            workspaceId = Workspace,
            sealedTask = computer.Browser.Task(taskId, Workspace, "Test", prompt)
        });

        var commandId = Guid.NewGuid().ToString();
        var start = await _owner.PostAsync<HostCommand>($"/api/tasks/{taskId}/start", new
        {
            commandId,
            @sealed = computer.Browser.Start(commandId, taskId, Workspace)
        });

        return (computer, RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId);
    }

    private async Task<(Computer Computer, string RunId)> RunningRunAsync()
    {
        var (computer, runId) = await QueuedRunAsync();

        await Hosts.PublishAsync(computer.Access, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 1, RemoteEventKind.Running,
            computer.Sealer.Detail(runId, 1, RemoteEventKind.Running, "Started")));

        return (computer, runId);
    }

    /// <summary>The computer stopping the run to ask its owner for permission.</summary>
    private async Task<string> AskAsync(Computer computer, string runId, bool remoteDecidable)
    {
        var approvalId = "approval-" + Guid.NewGuid().ToString("N");
        var action = new SealedAction(
            "run_command", "{\"command\":\"dotnet test\"}", "dotnet test", "C:\\work", "Run the tests");

        await Hosts.PublishAsync(computer.Access, new HostEvent(
            Guid.NewGuid().ToString("N"), runId, 2, RemoteEventKind.ApprovalRequested,
            computer.Sealer.Detail(runId, 2, RemoteEventKind.ApprovalRequested, "Run the tests"),
            new ApprovalRequest(approvalId, "call-1", ActionHash, remoteDecidable,
                computer.Sealer.Action(runId, approvalId, "call-1", ActionHash, remoteDecidable, action))));

        return approvalId;
    }

    /// <summary>
    /// The person's answer as the panel sends it: the decision in the clear for the panel to show
    /// what was sent, and sealed for the computer to act on.
    /// </summary>
    private static object Answer(Computer computer, string approvalId, string decision)
    {
        var commandId = Guid.NewGuid().ToString();

        return new
        {
            commandId,
            hostId = computer.Access.HostId,
            decision,
            actionHash = ActionHash,
            @sealed = computer.Browser.Decision(
                commandId, approvalId, ActionHash, Enum.Parse<RemoteDecision>(decision))
        };
    }

    private sealed record DeviceView(string Id, string Name, string Token);
}
