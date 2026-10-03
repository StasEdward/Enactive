namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Who a browser is: a session row, checked against the database on every request.
///
/// <para>The cookie only says which session it is. Whether that session still stands - the account is
/// active, the row is unrevoked and unexpired, the account's security version has not moved on - is
/// read from storage each time, because a cookie is a copy the browser keeps: revoking a session has to
/// stop it on the next request, not eight hours later when the copy expires.</para>
/// </summary>
public sealed class SessionTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private WebApplicationFactory<Program> _gateway = null!;

    private Database Db => new(database.ConnectionString);

    private SessionStore Sessions => new(Db, TimeProvider.System);

    private static string Name(string stem) => stem + "-" + Guid.NewGuid().ToString("N")[..8];

    public Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _gateway.DisposeAsync().AsTask();

    // ── the session, checked on every request ───────────────────────────────

    /// <summary>
    /// Revoked in the database, refused on the very next request - with the browser still holding a
    /// cookie that has hours left to run.
    /// </summary>
    [Fact]
    public async Task A_revoked_session_is_refused_on_its_next_request()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        await AssertAllowedAsync(browser);

        await Sessions.RevokeAsync(await OnlySessionOfAsync(browser.UserId), default);

        await AssertRefusedAsync(browser);
    }

    /// <summary>
    /// Every browser of the account, at once: each holds its own session, and all of them end. The
    /// security version moves on too, so a session row that somehow escaped the revocation would still
    /// not match its cookie.
    /// </summary>
    [Fact]
    public async Task Revoking_all_sessions_signs_out_every_browser()
    {
        var name = Name("alice");
        using var laptop = await PanelClient.SignedInAsync(_gateway, name);
        using var phone = await PanelClient.SignedInAsync(_gateway, name);
        using var bobs = await PanelClient.SignedInAsync(_gateway, Name("bob"));

        Assert.Equal(laptop.UserId, phone.UserId);
        var before = await VersionOfAsync(laptop.UserId);

        await Sessions.RevokeAllAsync(laptop.UserId, default);

        await AssertRefusedAsync(laptop);
        await AssertRefusedAsync(phone);
        await AssertAllowedAsync(bobs);
        Assert.Equal(before + 1, await VersionOfAsync(laptop.UserId));

        // And signing in again afterwards works: the new session is opened under the new version.
        using var again = await PanelClient.SignedInAsync(_gateway, name);
        await AssertAllowedAsync(again);
    }

    /// <summary>
    /// Disabling an account stops its browsers whatever their sessions say: the account's status is
    /// part of every request's check, not only of signing in.
    /// </summary>
    [Fact]
    public async Task A_disabled_account_is_refused_with_a_live_cookie()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        await AssertAllowedAsync(browser);

        await DisableAsync(browser.UserId);

        await AssertRefusedAsync(browser);
    }

    /// <summary>And a disabled account gets no new session either.</summary>
    [Fact]
    public async Task A_disabled_account_cannot_sign_in()
    {
        var name = Name("alice");
        using var first = await PanelClient.SignedInAsync(_gateway, name);
        await DisableAsync(first.UserId);

        using var second = new PanelClient(_gateway);
        await second.SessionAsync();
        using var refused = await second.SendAsync(HttpMethod.Post, "/api/dev/sign-in", new { name });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False((await second.SessionAsync()).Authenticated);
    }

    /// <summary>
    /// Signing out ends the session ROW, not only the cookie. A browser asked to delete its cookie may
    /// have been copied first, and a sign-out that only deleted it would leave the copy working until it
    /// expired.
    /// </summary>
    [Fact]
    public async Task Signing_out_revokes_the_session_so_a_kept_cookie_is_refused()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        using var copy = new PanelClient(_gateway);
        copy.Cookies.Add(browser.Http.BaseAddress!, browser.Cookies.GetCookies(browser.Http.BaseAddress!));
        await AssertAllowedAsync(copy);

        await browser.PostAsync("/api/logout", new { });

        Assert.False((await browser.SessionAsync()).Authenticated);
        await AssertRefusedAsync(copy);
    }

    /// <summary>
    /// Signing out on one browser ends that browser's session and no other: the person's phone stays
    /// signed in when they sign out of a borrowed laptop.
    /// </summary>
    [Fact]
    public async Task Logout_ends_this_session_only()
    {
        var name = Name("alice");
        using var laptop = await PanelClient.SignedInAsync(_gateway, name);
        using var phone = await PanelClient.SignedInAsync(_gateway, name);

        await laptop.PostAsync("/api/logout", new { });

        await AssertRefusedAsync(laptop);
        await AssertAllowedAsync(phone);
        Assert.Equal(1, await LiveSessionsOfAsync(phone.UserId));
    }

    /// <summary>
    /// Signing out everywhere ends every session of the account, the one asking included, and moves the
    /// security version on - and touches nobody else's. It is what a person does when a device is lost,
    /// so a cookie copied from any of their browsers must stop with it.
    /// </summary>
    [Fact]
    public async Task Logout_all_ends_every_session()
    {
        var name = Name("alice");
        using var laptop = await PanelClient.SignedInAsync(_gateway, name);
        using var phone = await PanelClient.SignedInAsync(_gateway, name);
        using var bobs = await PanelClient.SignedInAsync(_gateway, Name("bob"));
        using var copy = new PanelClient(_gateway);
        copy.Cookies.Add(laptop.Http.BaseAddress!, laptop.Cookies.GetCookies(laptop.Http.BaseAddress!));
        var before = await VersionOfAsync(laptop.UserId);

        await laptop.PostAsync("/api/logout-all", new { });

        await AssertRefusedAsync(laptop);
        await AssertRefusedAsync(copy);
        await AssertRefusedAsync(phone);
        await AssertAllowedAsync(bobs);
        Assert.Equal(0, await LiveSessionsOfAsync(laptop.UserId));
        Assert.Equal(before + 1, await VersionOfAsync(laptop.UserId));
    }

    /// <summary>
    /// Both sign-outs change state, so both carry the antiforgery token. Without it, any page the person
    /// visited could sign them out of everything, as often as it liked.
    /// </summary>
    [Theory]
    [InlineData("/api/logout")]
    [InlineData("/api/logout-all")]
    public async Task Signing_out_without_the_antiforgery_token_is_refused(string path)
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));

        using var response = await browser.SendAsync(HttpMethod.Post, path, new { }, csrf: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf", (await ErrorOfAsync(response)).Code);
        await AssertAllowedAsync(browser);
    }

    /// <summary>
    /// The account's security version alone decides: moved on with every session row left exactly as it
    /// was, unrevoked and unexpired, the cookie is refused. A revocation that only bumped the version - a
    /// session opened by a sign-in racing it, which the row update missed - is still a revocation.
    /// </summary>
    [Fact]
    public async Task A_security_version_moved_on_alone_refuses_the_cookie()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        await AssertAllowedAsync(browser);

        await database.ExecuteAsync(
            $"UPDATE users SET security_version = security_version + 1 WHERE id = '{browser.UserId}'");

        Assert.Equal(1, await LiveSessionsOfAsync(browser.UserId));
        await AssertRefusedAsync(browser);
    }

    // ── limits ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The API's limit is counted per person. Counted per address - every visitor behind the tunnel - or
    /// globally, Alice's busy panel would lock Bob out of his: the old shared lockout, by another name.
    /// The refusal is the API's own shape, so the panel can tell "wait" from "this is broken".
    /// </summary>
    [Fact]
    public async Task One_users_burst_does_not_limit_another_user()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        using var bob = await PanelClient.SignedInAsync(_gateway, Name("bob"));
        var statuses = new List<HttpStatusCode>();

        // Registering the browser's device is one of the minute's calls, as it is for the panel.
        await alice.EnsureDeviceAsync();

        for (var call = 1; call < RequestLimits.ApiPerMinute; call++)
        {
            using var state = await alice.SendAsync(HttpMethod.Get, "/api/state", csrf: false);
            statuses.Add(state.StatusCode);
        }

        using var refused = await alice.SendAsync(HttpMethod.Get, "/api/state", csrf: false);

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("rate-limited", (await ErrorOfAsync(refused)).Code);
        await AssertAllowedAsync(bob);
    }

    // ── refusals at the door ────────────────────────────────────────────────

    /// <summary>
    /// The cookie scheme refuses in the API's own shape: a status and a JSON body with a code, like every
    /// other refusal. A bare status left the panel with an empty body to parse, and "sign in again" and
    /// "you may not do that" looked alike. The 403 has no endpoint that reaches it today, so a test-only
    /// path asks the scheme to forbid.
    /// </summary>
    [Fact]
    public async Task A_refusal_at_the_door_is_coded_like_the_rest_of_the_api()
    {
        await using var gateway = TestGateway.Create(database, configure: builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, ForbidOnRequest>()));
        using var anonymous = new PanelClient(gateway);

        using var unauthenticated = await anonymous.SendAsync(HttpMethod.Get, "/api/state", csrf: false);
        using var forbidden = await anonymous.SendAsync(HttpMethod.Get, ForbidOnRequest.Path, csrf: false);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("unauthenticated", (await ErrorOfAsync(unauthenticated)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("forbidden", (await ErrorOfAsync(forbidden)).Code);
    }

    /// <summary>
    /// The session says who is signed in, so the browser can check it against the account its key
    /// store is open for, and says nobody when nobody is.
    /// </summary>
    [Fact]
    public async Task The_session_names_the_person_signed_in_and_nobody_otherwise()
    {
        var name = Name("alice");
        using var anonymous = new PanelClient(_gateway);
        using var browser = await PanelClient.SignedInAsync(_gateway, name);

        var nobody = await anonymous.SessionAsync();
        var somebody = await browser.SessionAsync();

        Assert.False(nobody.Authenticated);
        Assert.Null(nobody.User);
        Assert.False(string.IsNullOrEmpty(nobody.CsrfToken));

        Assert.True(somebody.Authenticated);
        Assert.Equal(browser.UserId, somebody.User!.Id);
        Assert.Equal(name, somebody.User.DisplayName);
    }

    /// <summary>
    /// The cookie is the browser's alone to send, and only to this host. Lax and not Strict: the OAuth
    /// callback is a top-level navigation from another site, and a Strict cookie set there would not be
    /// sent on the redirect that follows it.
    /// </summary>
    [Fact]
    public async Task The_cookie_is_http_only_lax_host_only_and_for_the_whole_site()
    {
        using var browser = new PanelClient(_gateway);
        await browser.SessionAsync();

        using var response = await browser.SendAsync(HttpMethod.Post, "/api/dev/sign-in", new { name = Name("alice") });
        response.EnsureSuccessStatusCode();

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Enactive.User=", StringComparison.Ordinal));
        var attributes = cookie.Split(';', StringSplitOptions.TrimEntries).Skip(1).Select(a => a.ToLowerInvariant()).ToArray();

        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Signing in is a state-changing call like any other, so it carries the antiforgery token. Without
    /// it, another site could sign a visitor in to an account of its choosing.
    /// </summary>
    [Fact]
    public async Task Signing_in_without_the_antiforgery_token_is_refused()
    {
        using var browser = new PanelClient(_gateway);
        await browser.SessionAsync();

        using var response = await browser.SendAsync(
            HttpMethod.Post, "/api/dev/sign-in", new { name = Name("alice") }, csrf: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await browser.SessionAsync()).Authenticated);
    }

    // ── the development sign-in ─────────────────────────────────────────────

    /// <summary>
    /// A sign-in with no provider behind it would hand any account to anyone who typed its name. So a
    /// gateway configured with it outside Development does not start at all, rather than starting and
    /// trusting whoever set the variable to have meant well.
    /// </summary>
    [Fact]
    public async Task Development_sign_in_refuses_to_start_in_production()
    {
        await using var gateway = TestGateway.Create(database, devSignIn: true,
            builder => builder.UseSetting("environment", "Production"));

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(DevelopmentSignIn.Setting, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A security switch set to something that is not true or false stops the start. Read as "off" it
    /// would hide a mistake; read as "on" it would sign anyone in as anybody.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    public async Task Development_sign_in_refuses_to_start_with_a_setting_that_is_not_a_boolean(string value)
    {
        await using var gateway = TestGateway.Create(database, devSignIn: false,
            builder => builder.UseSetting(DevelopmentSignIn.Setting, value));

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(DevelopmentSignIn.Setting, refused.Message, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The name is checked to its very end. A pattern ending in <c>$</c> accepts a name followed by one
    /// line break, so "alice\n" became an account of its own beside "alice", under a name that prints the
    /// same.
    /// </summary>
    [Fact]
    public async Task Development_sign_in_refuses_a_name_with_a_trailing_line_break()
    {
        var name = Name("alice");
        using var browser = new PanelClient(_gateway);
        await browser.SessionAsync();

        using var response = await browser.SendAsync(HttpMethod.Post, "/api/dev/sign-in", new { name = name + "\n" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await browser.SessionAsync()).Authenticated);
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM external_identities WHERE provider = 'dev' AND subject LIKE '{name}%'"));
    }

    /// <summary>Not configured, not there: the route does not exist, rather than existing and refusing.</summary>
    [Fact]
    public async Task Development_sign_in_is_absent_by_default()
    {
        await using var gateway = TestGateway.Create(database, devSignIn: false);
        using var browser = new PanelClient(gateway);
        await browser.SessionAsync();

        using var response = await browser.SendAsync(HttpMethod.Post, "/api/dev/sign-in", new { name = Name("alice") });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── the two kinds of caller stay apart ──────────────────────────────────

    /// <summary>
    /// A computer's token is a credential for the hub, and nothing else. A computer that could call the
    /// person's API could register more computers and start work on its own say-so.
    /// </summary>
    [Fact]
    public async Task A_host_token_cannot_call_the_user_api()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var computer = await browser.PostAsync<RegisteredHost>("/api/hosts", new { name = "Studio PC" });

        using var host = new PanelClient(_gateway);
        void Bearer(HttpRequestMessage request) => request.Headers.Authorization = new("Bearer", computer.Token);

        using var read = await host.SendAsync(HttpMethod.Get, "/api/state", csrf: false, configure: Bearer);
        using var write = await host.SendAsync(HttpMethod.Post, "/api/hosts", new { name = "Another" }, configure: Bearer);

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    /// <summary>
    /// And a person's cookie is not a computer. The hub takes its computer from the credential, so a
    /// browser let in would be a computer with no row behind it - or, worse, one made up by whoever
    /// holds the cookie.
    /// </summary>
    [Fact]
    public async Task A_user_cookie_cannot_open_the_host_hub()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        await AssertAllowedAsync(browser);

        using var negotiate = await browser.SendAsync(HttpMethod.Post, "/hubs/host/negotiate?negotiateVersion=1");

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    // ── the store ───────────────────────────────────────────────────────────

    /// <summary>
    /// Valid means this session, of this person, under this version, inside its lifetime - each part
    /// checked, because each is a different way for a cookie to outlive what it stood for.
    /// </summary>
    [Fact]
    public async Task A_session_is_valid_only_for_its_person_its_version_and_its_lifetime()
    {
        var accounts = new AccountService(Db, TimeProvider.System);
        var alice = await accounts.ProvisionWithoutAdmissionAsync("test", Name("alice"), "Alice", default);
        var bob = await accounts.ProvisionWithoutAdmissionAsync("test", Name("bob"), "Bob", default);

        // A whole millisecond, which is what DATETIME(3) stores. MySQL ROUNDS a finer time on insert, so
        // an expiry written from one could land just after the instant this test calls its end.
        var opened = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var session = await new SessionStore(Db, new FixedClock(opened)).OpenAsync(alice, TestAccounts.Provider, default);

        Assert.True(await Sessions.ValidAsync(alice, session.SessionId, 1, default));
        Assert.False(await Sessions.ValidAsync(bob, session.SessionId, 1, default));
        Assert.False(await Sessions.ValidAsync(alice, session.SessionId, 2, default));
        Assert.False(await Sessions.ValidAsync(alice, "no-such-session", 1, default));

        var almost = new SessionStore(Db, new FixedClock(opened + SessionStore.Lifetime - TimeSpan.FromSeconds(1)));
        var after = new SessionStore(Db, new FixedClock(opened + SessionStore.Lifetime));

        Assert.True(await almost.ValidAsync(alice, session.SessionId, 1, default));
        Assert.False(await after.ValidAsync(alice, session.SessionId, 1, default));
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static async Task AssertAllowedAsync(PanelClient browser)
    {
        using var state = await browser.SendAsync(HttpMethod.Get, "/api/state", csrf: false);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
    }

    private static async Task AssertRefusedAsync(PanelClient browser)
    {
        using var state = await browser.SendAsync(HttpMethod.Get, "/api/state", csrf: false);
        Assert.Equal(HttpStatusCode.Unauthorized, state.StatusCode);
    }

    private async Task<UserAccess> OnlySessionOfAsync(string userId)
        => new(userId, Assert.Single(await database.StringsAsync(
            $"SELECT id FROM user_sessions WHERE user_id = '{userId}' AND revoked_at IS NULL")));

    private async Task<long> VersionOfAsync(string userId)
        => await database.ScalarLongAsync($"SELECT security_version FROM users WHERE id = '{userId}'");

    private Task<long> LiveSessionsOfAsync(string userId)
        => database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM user_sessions WHERE user_id = '{userId}' AND revoked_at IS NULL");

    private static async Task<ErrorView> ErrorOfAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorView>(RemoteJson.Options))!;

    /// <summary>What every refusal on the API answers with. Both fields, because the wire refuses unknown ones.</summary>
    private sealed record ErrorView(string Code, string Error);

    /// <summary>
    /// A path that asks the cookie scheme to forbid, put in front of the gateway's own pipeline: nothing
    /// the gateway maps forbids a signed-in person today, and the scheme's answer still has to be right.
    /// </summary>
    private sealed class ForbidOnRequest : IStartupFilter
    {
        public const string Path = "/test/forbid";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                if (context.Request.Path == Path)
                {
                    await context.ForbidAsync(UserCookie.SchemeName);
                    return;
                }

                await inner(context);
            });

            next(app);
        };
    }

    private Task DisableAsync(string userId)
        => database.ExecuteAsync($"UPDATE users SET status = 'Disabled' WHERE id = '{userId}'");

    private sealed record RegisteredHost(string Id, string Name, string Token);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
