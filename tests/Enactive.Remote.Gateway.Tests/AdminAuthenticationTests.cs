namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Administration;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

public sealed class AdminAuthenticationTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private static readonly Uri Origin = new("https://admin.example.test");
    private const string Mfa = "urn:example:enforced-mfa";
    private FakeProviders _fake = null!;
    private readonly Clock _clock = new();
    private Database Db => new(database.ConnectionString);
    private AdminStore Store => new(Db, _clock);
    private AdminIdentity Identity
    {
        get
        {
            Assert.True(AdminIdentity.TryCreate(FakeProviders.GoogleAuthority, _fake.GoogleUser.Sub, out var identity));
            return identity!;
        }
    }
    public async Task InitializeAsync()
    {
        _fake = await FakeProviders.StartAsync();
        _fake.GoogleUser = new GoogleAccount(Ids.New(), "Admin", "admin@example.test");
        _fake.AuthenticationContext = Mfa;
        _fake.AuthenticationTime = _clock.GetUtcNow().ToUnixTimeSeconds();
    }
    public async Task DisposeAsync() => await _fake.DisposeAsync();

    [Fact]
    public async Task Verified_mfa_signs_in_and_logout_requires_csrf_and_revokes_copied_cookie()
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out var cookies);
        await Store.GrantAsync(Identity, default);
        using var callback = await LoginAsync(client);
        Assert.Equal("/admin/", callback.Headers.Location!.OriginalString);
        var cookieHeader = cookies.GetCookieHeader(Origin);
        var cookie = Assert.Single(cookies.GetCookies(Origin).Cast<Cookie>(), c => c.Name == AdminAuthentication.CookieName);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("/", cookie.Path);
        var setCookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"), v => v.StartsWith(AdminAuthentication.CookieName + "="));
        Assert.Contains("samesite=lax", setCookie.ToLowerInvariant());
        Assert.DoesNotContain("domain=", setCookie.ToLowerInvariant());
        var view = await SessionAsync(client);
        Assert.True(view.Authenticated);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/admin/api/fresh")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/api/signout", null)).StatusCode);
        Assert.True((await SessionAsync(client)).Authenticated);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", view.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/admin/api/signout", null)).StatusCode);
        using var copied = gateway.CreateDefaultClient(Origin);
        copied.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        Assert.False((await SessionAsync(copied)).Authenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/admin/api/fresh")).StatusCode);
    }

    [Fact]
    public async Task Cli_bootstrap_revoke_and_recovery_never_revive_old_sessions()
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        Assert.Equal(0, await AdminCommands.RunAsync(["administrators", "grant", Identity.Issuer, Identity.Subject], Db, TextWriter.Null));
        using var callback = await LoginAsync(client);
        Assert.True((await SessionAsync(client)).Authenticated);
        Assert.Equal(0, await AdminCommands.RunAsync(["administrators", "revoke", Identity.Issuer, Identity.Subject], Db, TextWriter.Null));
        Assert.Equal(0, await AdminCommands.RunAsync(["administrators", "grant", Identity.Issuer, Identity.Subject], Db, TextWriter.Null));
        Assert.False((await SessionAsync(client)).Authenticated);
        using var again = await LoginAsync(client);
        Assert.True((await SessionAsync(client)).Authenticated);
        Assert.Equal(3, await database.ScalarLongAsync("SELECT COUNT(*) FROM administrator_audit WHERE actor = 'operator' AND target = (SELECT id FROM administrators WHERE subject = '" + Identity.Subject + "')"));
    }

    [Fact]
    public async Task Fresh_authentication_expires_before_the_session_and_session_has_an_absolute_end()
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        await Store.GrantAsync(Identity, default);
        using var callback = await LoginAsync(client);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True((await SessionAsync(client)).Authenticated);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/api/fresh")).StatusCode);
        _clock.Advance(TimeSpan.FromMinutes(25));
        Assert.False((await SessionAsync(client)).Authenticated);
    }

    [Theory]
    [InlineData("missing_mfa")]
    [InlineData("wrong_mfa")]
    [InlineData("missing_time")]
    [InlineData("old_time")]
    [InlineData("future_time")]
    [InlineData("not_granted")]
    [InlineData("revoked")]
    public async Task Identity_without_current_authority_and_signed_mfa_cannot_create_a_session(string failure)
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        if (failure != "not_granted") await Store.GrantAsync(Identity, default);
        switch (failure)
        {
            case "missing_mfa": _fake.AuthenticationContext = null; break;
            case "wrong_mfa": _fake.AuthenticationContext = "password"; break;
            case "missing_time": _fake.AuthenticationTime = null; break;
            case "old_time": _fake.AuthenticationTime -= 600; break;
            case "future_time": _fake.AuthenticationTime += 600; break;
            case "revoked": await Store.RevokeAsync(Identity, default); break;
        }
        using var callback = await LoginAsync(client);
        Assert.Equal("/admin/?error=signin", callback.Headers.Location!.OriginalString);
        Assert.False((await SessionAsync(client)).Authenticated);
        await AssertNoSessionAsync();
    }

    [Theory]
    [InlineData(IdTokenFault.WrongSignature)]
    [InlineData(IdTokenFault.WrongAudience)]
    [InlineData(IdTokenFault.WrongIssuer)]
    [InlineData(IdTokenFault.Expired)]
    [InlineData(IdTokenFault.WrongNonce)]
    public async Task Invalid_signed_protocol_evidence_cannot_create_even_an_orphan_session(IdTokenFault fault)
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        await Store.GrantAsync(Identity, default);
        _fake.Fault = fault;
        using var callback = await LoginAsync(client);
        Assert.Equal("/admin/?error=signin", callback.Headers.Location!.OriginalString);
        Assert.False((await SessionAsync(client)).Authenticated);
        await AssertNoSessionAsync();
    }

    [Theory]
    [InlineData("/admin/")]
    [InlineData("/ADMIN/admin.js")]
    [InlineData("/admin/api/session")]
    [InlineData("/admin/auth/start")]
    [InlineData("/admin/auth/callback")]
    public async Task Public_host_and_disabled_feature_never_serve_admin_routes(string path)
    {
        await using var gateway = Gateway();
        using var publicClient = gateway.CreateDefaultClient(new Uri("https://panel.example.test"));
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync(path)).StatusCode);
        await using var disabled = TestGateway.Create(database);
        using var disabledClient = disabled.CreateDefaultClient(Origin);
        Assert.Equal(HttpStatusCode.NotFound, (await disabledClient.GetAsync(path)).StatusCode);
        Assert.Equal(0, _fake.TokenRequests);
    }

    [Fact]
    public async Task Admin_host_is_isolated_and_user_cookie_cannot_authorize_admin_requests()
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        foreach (var path in new[] { "/", "/index.html", "/api/session", "/auth/google/start" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        using var page = await client.GetAsync("/admin/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl!.NoStore);
        using var panel = await PanelClient.SignedInAsync(gateway, Ids.New());
        client.DefaultRequestHeaders.Add("Cookie", panel.Cookies.GetCookieHeader(panel.Http.BaseAddress!));
        Assert.False((await SessionAsync(client)).Authenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/api/fresh")).StatusCode);
    }

    [Fact]
    public async Task Callback_state_is_single_use_and_tampering_fails_before_token_exchange()
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        await Store.GrantAsync(Identity, default);
        var (target, fields) = await AnswerAsync(client);
        var tampered = new Dictionary<string, string>(fields) { ["state"] = "untrusted-state" };
        using var bad = await client.PostAsync(target, new FormUrlEncodedContent(tampered));
        Assert.Equal(0, _fake.TokenRequests);
        await AssertNoSessionAsync();
        using var good = await client.PostAsync(target, new FormUrlEncodedContent(fields));
        Assert.Equal("/admin/", good.Headers.Location!.OriginalString);
        using var replay = await client.PostAsync(target, new FormUrlEncodedContent(fields));
        Assert.Equal("/admin/?error=signin", replay.Headers.Location!.OriginalString);
        Assert.Equal(1, _fake.TokenRequests);
    }

    [Fact]
    public async Task Replayed_callback_cookies_cannot_bypass_the_front_door_rate_limit()
    {
        await using var gateway = Gateway();
        using var browser = Browser(gateway, out var cookies);
        var (target, fields) = await AnswerAsync(browser);
        fields["code"] = "invalid-code";
        using var replay = gateway.CreateDefaultClient(Origin);
        replay.DefaultRequestHeaders.Add("Cookie", cookies.GetCookieHeader(target));
        for (var attempt = 0; attempt <= ExternalSignIn.CallbacksPerMinute; attempt++)
        {
            using var response = await replay.PostAsync(target, new FormUrlEncodedContent(fields));
            Assert.Equal(attempt < ExternalSignIn.CallbacksPerMinute ? HttpStatusCode.Redirect : HttpStatusCode.TooManyRequests,
                response.StatusCode);
        }
        Assert.Equal(ExternalSignIn.CallbacksPerMinute, _fake.TokenRequests);
        await AssertNoSessionAsync();
    }

    [Theory]
    [InlineData("/admin/api/overview")]
    [InlineData("/admin/api/users")]
    [InlineData("/admin/api/registrations")]
    [InlineData("/admin/api/users/00000000000000000000000000000000")]
    public async Task Directory_endpoints_require_a_live_administrator_on_the_admin_host(string path)
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        using var panel = await PanelClient.SignedInAsync(gateway, Ids.New());
        client.DefaultRequestHeaders.Add("Cookie", panel.Cookies.GetCookieHeader(panel.Http.BaseAddress!));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        using var publicClient = gateway.CreateDefaultClient(new Uri("https://panel.example.test"));
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync(path)).StatusCode);
        await Store.GrantAsync(Identity, default);
        using var callback = await LoginAsync(client);
        using var authorized = await client.GetAsync(path);
        Assert.Equal(path.EndsWith(new string('0', 32)) ? HttpStatusCode.NotFound : HttpStatusCode.OK, authorized.StatusCode);
        Assert.True(authorized.Headers.CacheControl!.NoStore);
        await Store.RevokeAsync(Identity, default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Directory_responses_have_only_the_allowed_metadata_and_reject_invalid_input()
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        await Store.GrantAsync(Identity, default);
        using var callback = await LoginAsync(client);
        var json = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/admin/api/users?search=" + user.UserId);
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "createdAt", "displayName", "id", "sealedBytes", "status" },
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var detail = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/admin/api/users/" + user.UserId);
        Assert.Equal(new[] { "devices", "hosts", "lastHostSeenAt", "runs", "tasks", "user" },
            detail.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var path in new[] { "/users?size=101", "/users?size=abc", "/registrations?state=Active", "/registrations?after=bad" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/api" + path)).StatusCode);
    }

    [Fact]
    public async Task Directory_reads_are_bounded_without_exhausting_the_logout_budget()
    {
        await using var gateway = Gateway();
        using var client = Browser(gateway, out _);
        await Store.GrantAsync(Identity, default);
        using var callback = await LoginAsync(client);
        var session = await SessionAsync(client);
        for (var i = 0; i < RequestLimits.AdminReadsPerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/admin/api/users")).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/admin/api/signout", null)).StatusCode);
    }

    private async Task AssertNoSessionAsync() => Assert.Equal(0, await database.ScalarLongAsync(
        "SELECT COUNT(*) FROM administrator_sessions WHERE administrator_id IN (SELECT id FROM administrators WHERE subject = '" + Identity.Subject + "')"));
    private WebApplicationFactory<Program> Gateway() => TestGateway.Create(database, configure: builder =>
    {
        builder.UseSetting(AdminSettings.OriginKey, Origin.ToString());
        builder.UseSetting(AdminSettings.AuthorityKey, FakeProviders.GoogleAuthority);
        builder.UseSetting(AdminSettings.ClientIdKey, FakeProviders.GoogleClientId);
        builder.UseSetting(AdminSettings.ClientSecretKey, FakeProviders.GoogleClientSecret);
        builder.UseSetting(AdminSettings.MfaAcrKey, Mfa);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.Configure<OpenIdConnectOptions>(AdminAuthentication.OidcScheme,
                o => o.BackchannelHttpHandler = _fake.CreateHandler());
        });
    });
    private static HttpClient Browser(WebApplicationFactory<Program> gateway, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return gateway.CreateDefaultClient(Origin, new CookieContainerHandler(cookies));
    }
    private async Task<(Uri, Dictionary<string, string>)> AnswerAsync(HttpClient client)
    {
        using var start = await client.GetAsync("/admin/auth/start?returnUrl=https://attacker.example");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        Assert.Equal("login", query["prompt"]);
        Assert.Equal("0", query["max_age"]);
        Assert.Equal(Mfa, query["acr_values"]);
        Assert.Equal(new Uri(Origin, AdminAuthentication.CallbackPath).AbsoluteUri, query["redirect_uri"]);
        using var provider = _fake.CreateBrowser();
        using var response = await provider.GetAsync(start.Headers.Location);
        var (target, fields) = FakeProviders.ReadFormPost(await response.Content.ReadAsStringAsync());
        return (target, new Dictionary<string, string>(fields));
    }
    private async Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        var (target, fields) = await AnswerAsync(client);
        return await client.PostAsync(target, new FormUrlEncodedContent(fields));
    }
    private static async Task<View> SessionAsync(HttpClient client)
        => (await client.GetFromJsonAsync<View>("/admin/api/session"))!;
    private sealed record View(bool Authenticated, string? AdministratorId, DateTimeOffset? ExpiresAt, string CsrfToken);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}
