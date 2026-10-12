namespace Enactive.Server.Tests;

using System.Net;
using System.Text.RegularExpressions;
using Enactive.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Signing in through the service's real pipeline (server plan 1.3): every page is behind it, a right password gets a
/// session, a session ends when its account is disabled - on its next request, not when the cookie runs out.
/// </summary>
public sealed partial class SignInTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-signin", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private UserStore _users = null!;

    private const string Password = "correct horse battery";

    private sealed class FromInside : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, go) => { context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.0.9"); return go(); });
            next(app);
        };
    }

    public async Task InitializeAsync()
    {
        var options = new ServerOptions
        {
            Listen = ["0.0.0.0:8443"], AllowedSubnets = ["10.20.0.0/16"], HostNames = ["localhost"],
            Certificate = new CertificateOptions(Path: "server.pfx"),
            // The workspaces the old addresses name are made under the test's folder: the browser's are only those under the roots.
            WorkspaceRoots = [_root]
        };
        _app = ServerApp.Build([], options, _root, certificate: null, b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddTransient<IStartupFilter, FromInside>();
        });
        await _app.StartAsync();
        _users = _app.Services.GetRequiredService<UserStore>();
        _users.Add("alice", Password, Role.Operator);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>A browser enough for these tests: it keeps cookies and does not follow redirects, so they can be read.</summary>
    private sealed class Browser(HttpClient client)
    {
        private readonly Dictionary<string, string> _cookies = new();

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (_cookies.Count > 0)
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var cookie in set)
                {
                    var pair = cookie.Split(';')[0].Split('=', 2);
                    if (pair[1].Length == 0 || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                        _cookies.Remove(pair[0]);
                    else
                        _cookies[pair[0]] = pair[1];
                }
            return response;
        }

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

        public Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form)
            => SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) });

        public IReadOnlyCollection<string> Cookies => _cookies.Keys;
    }

    private Browser NewBrowser()
    {
        var client = _app.GetTestServer().CreateClient();
        client.BaseAddress = new Uri("https://localhost/");
        return new Browser(client);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static async Task<string> TokenFromAsync(HttpResponseMessage page)
        => Token().Match(await page.Content.ReadAsStringAsync()).Groups[1].Value;

    private static async Task<HttpResponseMessage> SignInAsync(Browser browser, string name, string password)
    {
        var token = await TokenFromAsync(await browser.GetAsync("/signin"));
        return await browser.PostAsync("/signin", new()
        {
            ["_handler"] = "signin",
            ["__RequestVerificationToken"] = token,
            ["Input.Name"] = name,
            ["Input.Password"] = password
        });
    }

    [Fact]
    public async Task A_page_sends_somebody_not_signed_in_to_sign_in_and_back()
    {
        var response = await NewBrowser().GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/signin?ReturnUrl=%2F", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_right_password_gives_a_session_that_opens_the_pages()
    {
        var browser = NewBrowser();

        var signedIn = await SignInAsync(browser, "alice", Password);

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains("enactive", browser.Cookies);
        // The page itself, not a way to sign-in: what it shows is drawn once its connection is up (bUnit tests).
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "signed in", Remote: "10.20.0.9" });
    }

    /// <summary>
    /// Every page is live and drawn only once its connection is up - a page drawn ahead of it took text and clicks that
    /// were lost - except sign-in, which is drawn and posted as a plain form: signing in sets a cookie, which only a real
    /// response can do, and a page before sign-in has no connection to be live on (the hub refuses it).
    /// </summary>
    [Fact]
    public async Task Every_page_is_live_but_sign_in()
    {
        var browser = NewBrowser();

        var signIn = await (await browser.GetAsync("/signin")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!--Blazor:", signIn);
        Assert.Contains("name=\"Input.Name\"", signIn);

        await SignInAsync(browser, "alice", Password);
        var home = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("<!--Blazor:{\"type\":\"server\"", home);
        Assert.DoesNotContain("prerenderId", home);
        // The band Blazor's script shows when a page's connection fails on an error no page caught: without it the page
        // stayed as it was, saying "Connected", every button dead.
        Assert.Matches("<div id=\"blazor-error-ui\"[^>]*>[^<]*<span>This page stopped working", home);
        Assert.Contains("onclick=\"location.reload()\">Reload</button>", home);
    }

    [Fact]
    public async Task The_session_cookie_is_secure_http_only_and_strict()
    {
        var browser = NewBrowser();
        var signedIn = await SignInAsync(browser, "alice", Password);

        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("enactive="));

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_password_gets_no_session_and_one_sentence()
    {
        var browser = NewBrowser();

        var response = await SignInAsync(browser, "alice", "not the password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("the name or the password is not right", body);
        Assert.DoesNotContain("enactive", browser.Cookies);
        // The page drawn again for another try holds no password: what was typed is not written back into the box.
        Assert.DoesNotContain("not the password", body);
    }

    /// <summary>Locked out: the page says to wait, and is drawn again with neither the password typed nor any other.</summary>
    [Fact]
    public async Task A_locked_sign_in_writes_no_password_back_into_the_page()
    {
        var browser = NewBrowser();
        HttpResponseMessage response = null!;
        for (var i = 0; i <= UserStore.FailuresBeforeLockout; i++)
            response = await SignInAsync(browser, "alice", $"wrong guess {i}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Too many wrong passwords.", body);
        Assert.DoesNotContain("wrong guess", body);
    }

    /// <summary>Disabled while signed in: the very next request is a signed-out one, not the next morning's.</summary>
    [Fact]
    public async Task A_session_ends_on_its_next_request_once_its_account_is_disabled()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        _users.SetDisabled("alice", true);

        var response = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/signin", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_lowered_role_is_lowered_now()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        _users.SetRole("alice", Role.Viewer);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
    }

    /// <summary>Signing out is a form post of the page itself; a post without its token is refused.</summary>
    [Fact]
    public async Task Signing_out_needs_the_page_s_token()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/signout", new())).StatusCode);

        // A token for the person signed in, from the one page still sent whole: a live page's form gets its token
        // over the connection, which this test has none of.
        var token = await TokenFromAsync(await browser.GetAsync("/signin"));
        var out_ = await browser.PostAsync("/signout", new() { ["__RequestVerificationToken"] = token });
        Assert.Equal(HttpStatusCode.Redirect, out_.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
    }

    /// <summary>Only a path on this site: a link that says where to go after sign-in cannot send the person elsewhere.</summary>
    [Fact]
    public async Task After_sign_in_the_person_is_sent_only_somewhere_on_this_site()
    {
        var browser = NewBrowser();
        var token = await TokenFromAsync(await browser.GetAsync("/signin"));

        var response = await browser.PostAsync("/signin?ReturnUrl=%2F%2Fevil.example", new()
        {
            ["_handler"] = "signin", ["__RequestVerificationToken"] = token,
            ["Input.Name"] = "alice", ["Input.Password"] = Password
        });

        Assert.Equal("/", response.Headers.Location!.OriginalString.Replace("https://localhost", ""));
    }

    /// <summary>A live page's connection (the browser hub) is for somebody signed in; it was handed to anybody who asked.</summary>
    [Fact]
    public async Task The_browser_hub_is_only_for_somebody_signed_in()
    {
        HttpRequestMessage Negotiate()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
            request.Headers.Add("Origin", "https://localhost");
            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await NewBrowser().SendAsync(Negotiate())).StatusCode);

        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(Negotiate())).StatusCode);
    }

    // ── reading (server plan 1.6) ───────────────────────────────────────────

    // What the reading pages show is ReadingPagesTests': they are live, and the page the service sends holds none of it.

    [Fact]
    public async Task The_reading_pages_need_a_signed_in_person()
    {
        // The old addresses too: what a workspace's id is, is not told to somebody who has not signed in.
        foreach (var address in new[] { "/", "/history?ws=x", "/workspaces", "/history/run?ws=x&id=x", "/file?ws=x&path=y", "/library/template?ws=x",
                     "/library?ws=x", "/library/schedule?ws=x&id=x", "/logs", "/admin/settings", "/admin/users", "/admin/audit" })
        {
            var response = await NewBrowser().GetAsync(address);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("https://localhost/signin", response.Headers.Location!.ToString());
        }
    }

    /// <summary>
    /// A link in an earlier mail or a bookmark still lands: the history and the workspace list are the Runs screen now,
    /// which names a workspace by its id, never its path. What the old page was told to say does not come along; a folder
    /// the service does not know is not looked up on the disk.
    /// </summary>
    [Fact]
    public async Task An_old_history_address_lands_on_the_runs_screen_by_the_workspace_s_id()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);
        _app.Services.GetRequiredService<Runs.ServerEngine>().Workspaces.Touch(folder);
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        var known = await browser.GetAsync("/history?ws=" + Uri.EscapeDataString(folder) + "&said=" + Uri.EscapeDataString("Forgotten."));
        Assert.Equal(HttpStatusCode.Redirect, known.StatusCode);
        // What the old page was told to say stays behind: any link could put words there for the service to say.
        Assert.Equal($"/?ws={Enactive.Core.Context.WorkspaceInfo.For(folder).Id}", known.Headers.Location!.OriginalString);

        var unknown = await browser.GetAsync("/history?ws=" + Uri.EscapeDataString(Path.Combine(_root, "elsewhere")));
        Assert.Equal("/", unknown.Headers.Location!.OriginalString);
        Assert.Equal("/", (await browser.GetAsync("/workspaces")).Headers.Location!.OriginalString);
    }

    /// <summary>
    /// A folder the service's registry knows but that is not under its roots - a desktop's under the same account, or one
    /// left after the roots were narrowed - is not the browser's: an old address naming it carries no workspace on, and
    /// lands where a folder the service never knew does.
    /// </summary>
    [Fact]
    public async Task An_old_address_naming_a_known_folder_outside_the_roots_names_no_workspace()
    {
        var outside = Path.Combine(Path.GetTempPath(), "enactive-signin-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            _app.Services.GetRequiredService<Runs.ServerEngine>().Workspaces.Touch(outside);
            var browser = NewBrowser();
            await SignInAsync(browser, "alice", Password);

            var ws = Uri.EscapeDataString(outside);
            Assert.Equal("/", (await browser.GetAsync($"/history?ws={ws}")).Headers.Location!.OriginalString);
            Assert.Equal("/", (await browser.GetAsync($"/history/run?ws={ws}&id={Guid.NewGuid()}")).Headers.Location!.OriginalString);
            Assert.Equal("/proposals", (await browser.GetAsync($"/proposals/view?ws={ws}&set={Guid.NewGuid():N}")).Headers.Location!.OriginalString);
            Assert.Equal("/templates", (await browser.GetAsync($"/library/template?ws={ws}&id=digest")).Headers.Location!.OriginalString);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>
    /// A past run's old address - and a file's, which was read on a page of its own and is reviewed on the past run now -
    /// lands on the Runs screen, the workspace named by its id. A path the service does not know is not looked up on the
    /// disk: it lands on the Runs screen as it opens.
    /// </summary>
    [Fact]
    public async Task An_old_past_run_address_lands_on_the_new_one()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);
        _app.Services.GetRequiredService<Runs.ServerEngine>().Workspaces.Touch(folder);
        var id = Enactive.Core.Context.WorkspaceInfo.For(folder).Id;
        var run = Guid.NewGuid();
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        var past = await browser.GetAsync($"/history/run?ws={Uri.EscapeDataString(folder)}&id={run}");
        Assert.Equal(HttpStatusCode.Redirect, past.StatusCode);
        Assert.Equal($"/history/{id}/{run}", past.Headers.Location!.OriginalString);

        var file = await browser.GetAsync($"/file?ws={Uri.EscapeDataString(folder)}&path=digest.md");
        Assert.Equal(HttpStatusCode.Redirect, file.StatusCode);
        Assert.Equal($"/?ws={id}", file.Headers.Location!.OriginalString);

        var elsewhere = Uri.EscapeDataString(Path.Combine(_root, "elsewhere"));
        Assert.Equal("/", (await browser.GetAsync($"/history/run?ws={elsewhere}&id={run}")).Headers.Location!.OriginalString);
        Assert.Equal("/", (await browser.GetAsync($"/file?ws={elsewhere}&path=digest.md")).Headers.Location!.OriginalString);
        // The run's address with no run in it names only the workspace.
        Assert.Equal($"/?ws={id}", (await browser.GetAsync($"/history/run?ws={Uri.EscapeDataString(folder)}&id=x")).Headers.Location!.OriginalString);
    }

    /// <summary>
    /// A set's old address (the workspace by its path, the set in a query) lands on the set's page, the workspace named by its
    /// id. A path the service does not know - or no set - lands on the list, and no path is looked up on the disk.
    /// </summary>
    [Fact]
    public async Task An_old_proposals_address_lands_on_the_set_by_the_workspace_s_id()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);
        _app.Services.GetRequiredService<Runs.ServerEngine>().Workspaces.Touch(folder);
        var id = Enactive.Core.Context.WorkspaceInfo.For(folder).Id;
        var set = Guid.NewGuid().ToString("N");
        var browser = NewBrowser();
        Assert.StartsWith("https://localhost/signin", (await browser.GetAsync("/proposals/view?ws=x&set=y")).Headers.Location!.ToString());
        await SignInAsync(browser, "alice", Password);

        var known = await browser.GetAsync($"/proposals/view?ws={Uri.EscapeDataString(folder)}&set={set}");
        Assert.Equal(HttpStatusCode.Redirect, known.StatusCode);
        Assert.Equal($"/proposals/{id}/{set}", known.Headers.Location!.OriginalString);

        var elsewhere = Uri.EscapeDataString(Path.Combine(_root, "elsewhere"));
        Assert.Equal("/proposals", (await browser.GetAsync($"/proposals/view?ws={elsewhere}&set={set}")).Headers.Location!.OriginalString);
        Assert.Equal("/proposals", (await browser.GetAsync($"/proposals/view?ws={Uri.EscapeDataString(folder)}")).Headers.Location!.OriginalString);
        // Not an address a set is named in: only a run's id, so no name walks out of the folder.
        Assert.Equal("/proposals", (await browser.GetAsync($"/proposals/view?ws={Uri.EscapeDataString(folder)}&set=..")).Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The old template page's address (the workspace by its path, the template in a query) lands on the template in the
    /// Templates screen's editor, the workspace named by its id - and with no template, on a new one, as the old page
    /// opened. A path the service does not know lands on the Templates screen as it opens, and is not looked up on the disk.
    /// </summary>
    [Fact]
    public async Task An_old_template_address_lands_on_the_template_by_the_workspace_s_id()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);
        _app.Services.GetRequiredService<Runs.ServerEngine>().Workspaces.Touch(folder);
        var id = Enactive.Core.Context.WorkspaceInfo.For(folder).Id;
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        var known = await browser.GetAsync($"/library/template?ws={Uri.EscapeDataString(folder)}&id=digest");
        Assert.Equal(HttpStatusCode.Redirect, known.StatusCode);
        Assert.Equal($"/templates/digest?ws={id}", known.Headers.Location!.OriginalString);
        Assert.Equal($"/templates/New?ws={id}",
            (await browser.GetAsync($"/library/template?ws={Uri.EscapeDataString(folder)}")).Headers.Location!.OriginalString);

        var elsewhere = Uri.EscapeDataString(Path.Combine(_root, "elsewhere"));
        Assert.Equal("/templates", (await browser.GetAsync($"/library/template?ws={elsewhere}&id=digest")).Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The old schedules page's address, and a schedule's form's (the workspace by its path, the schedule in a query), land
    /// on the Schedules screen - the workspace named by its id, on the schedule when one was named. A path the service does
    /// not know lands on the Schedules screen as it opens, and is not looked up on the disk.
    /// </summary>
    [Fact]
    public async Task An_old_schedules_address_lands_on_the_schedules_screen_by_the_workspace_s_id()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);
        _app.Services.GetRequiredService<Runs.ServerEngine>().Workspaces.Touch(folder);
        var id = Enactive.Core.Context.WorkspaceInfo.For(folder).Id;
        var schedule = Guid.NewGuid();
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        var known = await browser.GetAsync($"/library?ws={Uri.EscapeDataString(folder)}");
        Assert.Equal(HttpStatusCode.Redirect, known.StatusCode);
        Assert.Equal($"/schedules?ws={id}", known.Headers.Location!.OriginalString);
        Assert.Equal($"/schedules/{schedule}?ws={id}",
            (await browser.GetAsync($"/library/schedule?ws={Uri.EscapeDataString(folder)}&id={schedule}")).Headers.Location!.OriginalString);
        Assert.Equal($"/schedules?ws={id}",
            (await browser.GetAsync($"/library/schedule?ws={Uri.EscapeDataString(folder)}")).Headers.Location!.OriginalString);

        var elsewhere = Uri.EscapeDataString(Path.Combine(_root, "elsewhere"));
        Assert.Equal("/schedules", (await browser.GetAsync($"/library?ws={elsewhere}")).Headers.Location!.OriginalString);
        Assert.Equal("/schedules", (await browser.GetAsync($"/library/schedule?ws={elsewhere}&id={schedule}")).Headers.Location!.OriginalString);
    }

    /// <summary>The old log page's address lands on the log, at the address the rail names it by.</summary>
    [Fact]
    public async Task The_old_log_address_lands_on_the_log()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        var old = await browser.GetAsync("/logs");

        Assert.Equal(HttpStatusCode.Redirect, old.StatusCode);
        Assert.Equal("/log", old.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The old settings page's address lands on the settings, at the address the rail names them by - where anybody but
    /// an Admin is told the role does not include them, as the old page told them.
    /// </summary>
    [Fact]
    public async Task The_old_settings_address_lands_on_the_settings()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        var old = await browser.GetAsync("/admin/settings");

        Assert.Equal(HttpStatusCode.Redirect, old.StatusCode);
        Assert.Equal("/settings", old.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The old accounts and audit pages' addresses land on the pages the rail names - where anybody but an Admin is told
    /// the role does not include them, as the old pages told them.
    /// </summary>
    [Fact]
    public async Task The_old_accounts_and_audit_addresses_land_on_their_pages()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        var accounts = await browser.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.Redirect, accounts.StatusCode);
        Assert.Equal("/accounts", accounts.Headers.Location!.OriginalString);

        var audit = await browser.GetAsync("/admin/audit");
        Assert.Equal(HttpStatusCode.Redirect, audit.StatusCode);
        Assert.Equal("/audit", audit.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// Every old address lands on a page that is there: each is a link somebody kept - a bookmark, an earlier mail - and a
    /// redirect to an address no page answers is that link dead with an extra step.
    /// </summary>
    [Fact]
    public async Task Every_old_address_lands_on_a_page_that_answers()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);
        _app.Services.GetRequiredService<Runs.ServerEngine>().Workspaces.Touch(folder);
        var ws = Uri.EscapeDataString(folder);
        // An Admin: every page is theirs to open, so a page that refuses a role is not mistaken for one that is not there.
        _users.Add("ada", Password, Role.Admin);
        var browser = NewBrowser();
        await SignInAsync(browser, "ada", Password);

        foreach (var old in new[]
                 {
                     $"/history?ws={ws}", "/workspaces", $"/history/run?ws={ws}&id={Guid.NewGuid()}", $"/file?ws={ws}&path=digest.md",
                     $"/proposals/view?ws={ws}&set={Guid.NewGuid():N}", $"/library/template?ws={ws}&id=digest", $"/library/template?ws={ws}",
                     $"/library?ws={ws}", $"/library/schedule?ws={ws}&id={Guid.NewGuid()}", "/logs", "/admin/settings", "/admin/users", "/admin/audit"
                 })
        {
            var moved = await browser.GetAsync(old);
            Assert.True(moved.StatusCode == HttpStatusCode.Redirect, $"{old} answered {moved.StatusCode}");
            var landed = await browser.GetAsync(moved.Headers.Location!.OriginalString);
            Assert.True(landed.StatusCode == HttpStatusCode.OK, $"{old} -> {moved.Headers.Location} answered {landed.StatusCode}");
        }
    }

    /// <summary>
    /// What the service answers besides its pages: signing out, its health, and the old addresses sent on. The pages the web
    /// UI replaced posted their forms to endpoints of their own, each its own check of who may do what beside the services';
    /// one left behind is a second way in that no page uses and no test watches.
    /// </summary>
    [Fact]
    public void Only_sign_out_health_and_the_old_addresses_are_endpoints_of_their_own()
    {
        var endpoints = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            // The pages (a component's route) and the files (the static assets) are not endpoints of the service's own.
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>() is null
                        && e.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.RootComponentMetadata>() is null
                        && e.Metadata.GetMetadata<Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor>() is null
                        // The framework's own: the hub's (/_blazor) and its redirect helper (/_framework).
                        && !(e.RoutePattern.RawText ?? "").TrimStart('/').StartsWith("_blazor", StringComparison.Ordinal)
                        && !(e.RoutePattern.RawText ?? "").TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
                        // The static files' own catch-all, for a file the build manifest names (UseStaticWebAssets).
                        && e.RoutePattern.RawText != "{**path:file}")
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[]
        {
            "GET /admin/audit", "GET /admin/settings", "GET /admin/users", "GET /file", "GET /health", "GET /history", "GET /history/run",
            "GET /library", "GET /library/schedule", "GET /library/template", "GET /logs", "GET /proposals/view", "GET /workspaces",
            "POST /signout"
        }.Order(StringComparer.Ordinal), endpoints);
    }

    /// <summary>
    /// The accounts page's old form post is gone with the page: a change to an account is made only on the live page,
    /// through AccountAdmin as the person. A post an Admin's browser still sends there changes nothing.
    /// </summary>
    [Fact]
    public async Task The_old_accounts_post_changes_nothing()
    {
        _users.Add("ada", Password, Role.Admin);
        var browser = NewBrowser();
        await SignInAsync(browser, "ada", Password);
        var token = await TokenFromAsync(await browser.GetAsync("/signin"));

        var response = await browser.PostAsync("/admin/users/act", new()
        {
            ["__RequestVerificationToken"] = token, ["act"] = "disable", ["name"] = "alice"
        });

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(_users.Find("alice")!.Disabled);
        Assert.DoesNotContain(_users.AuditTrail(), e => e.Action == "account disable");
    }

    /// <summary>
    /// Sign-in stands on its own: nobody is signed in yet, so there is no top bar to name them and no rail of sections to
    /// offer - only the brand and the form, in the theme. Drawn in the shell, it had a top bar that named nobody beside an
    /// empty rail.
    /// </summary>
    [Fact]
    public async Task Sign_in_is_the_brand_and_the_form_without_the_shell()
    {
        var page = await (await NewBrowser().GetAsync("/signin")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("class=\"shell\"", page);
        Assert.DoesNotContain("class=\"top\"", page);
        Assert.DoesNotContain("class=\"rail\"", page);
        Assert.Contains("brand/mark-loop.svg", page);
        Assert.Contains("class=\"signin-card\"", page);
        Assert.Contains("autocomplete=\"current-password\"", page);
    }

    [Fact]
    public async Task Health_answers_without_signing_in()
        => Assert.Equal(HttpStatusCode.OK, (await NewBrowser().GetAsync("/health")).StatusCode);
}
