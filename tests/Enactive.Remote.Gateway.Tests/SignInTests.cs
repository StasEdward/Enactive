namespace Enactive.Remote.Gateway.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Signing in with GitHub and Google, end to end against fake providers: the real handlers, the real
/// admission list and the real session, with a test playing the browser between them.
///
/// <para>The handlers are Microsoft's, and what they check - state, PKCE, the id token's signature,
/// issuer, audience, expiry and nonce - is shown here rather than assumed: each test hands the
/// gateway what an attacker would, and looks for the absence of a session afterwards.</para>
/// </summary>
public sealed class SignInTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    // The gateway as the browser sees it. https, because the handlers' correlation and nonce cookies are
    // Secure whatever the environment, and a cookie jar does not send a Secure cookie over http.
    private static readonly Uri Origin = new("https://localhost");

    private FakeProviders _fake = null!;

    private Database Db => new(database.ConnectionString);

    public async Task InitializeAsync() => _fake = await FakeProviders.StartAsync();

    public async Task DisposeAsync() => await _fake.DisposeAsync();

    // ── GitHub ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The account is the numeric id. Renamed on GitHub, the same person signs in to the same account,
    /// under the new name; nothing about them is found by the login.
    /// </summary>
    [Fact]
    public async Task Github_sign_in_creates_one_account_by_numeric_id()
    {
        await using var gateway = Gateway();
        var id = NewGitHubId();
        await ApproveAsync($"github:{id}");

        _fake.GitHubUser = new GitHubAccount(id, "octocat");
        using var first = new Browser(gateway);
        Assert.Equal("/", await first.SignInWithGitHubAsync(_fake));
        var session = await first.SessionAsync();
        Assert.True(session.Authenticated);
        Assert.Equal("octocat", session.User!.DisplayName);

        _fake.GitHubUser = new GitHubAccount(id, "octocat-renamed");
        using var second = new Browser(gateway);
        Assert.Equal("/", await second.SignInWithGitHubAsync(_fake));
        Assert.Equal(session.User.Id, (await second.SessionAsync()).User!.Id);

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM external_identities WHERE provider = 'github' AND subject = '{id}'"));
        Assert.Equal("octocat-renamed", Assert.Single(await database.StringsAsync(
            $"SELECT display FROM external_identities WHERE provider = 'github' AND subject = '{id}'")));

        // The provider's answer is carried for one hop and no further.
        Assert.False(second.Has(ExternalSignIn.CookieName));
    }

    /// <summary>
    /// A state the gateway did not issue to this browser gets nobody in: garbage, and also a real state
    /// issued to another browser, which is what a login-CSRF attacker would deliver. The code is real
    /// both times, so only the state check stands between it and a session.
    /// </summary>
    [Theory]
    [InlineData("garbage")]
    [InlineData("another browser's")]
    public async Task A_forged_state_creates_no_session(string forgery)
    {
        await using var gateway = Gateway();
        var id = NewGitHubId();
        await ApproveAsync($"github:{id}");
        _fake.GitHubUser = new GitHubAccount(id, "mallory");

        using var victim = new Browser(gateway);
        var callback = await victim.GitHubCallbackAsync(_fake);

        string forged;
        if (forgery == "garbage")
        {
            forged = "forged-state";
        }
        else
        {
            using var attacker = new Browser(gateway);
            forged = Query(await attacker.GitHubCallbackAsync(_fake), "state");
        }

        Assert.Equal("/#failed", await victim.FollowAsync(WithQuery(callback, "state", forged)));

        Assert.False((await victim.SessionAsync()).Authenticated);
        Assert.Equal(0, await IdentitiesAsync("github", id.ToString()));
    }

    /// <summary>
    /// A code issued for somebody else's sign-in, delivered with this browser's own valid state - a code
    /// stolen in flight and injected. The state is good; the PKCE verifier this browser holds does not
    /// match the challenge the code was issued under, so the provider refuses to redeem it.
    /// </summary>
    [Fact]
    public async Task A_wrong_code_verifier_creates_no_session()
    {
        await using var gateway = Gateway();
        var id = NewGitHubId();
        await ApproveAsync($"github:{id}");
        _fake.GitHubUser = new GitHubAccount(id, "mallory");

        using var victim = new Browser(gateway);
        using var attacker = new Browser(gateway);
        var victims = await victim.GitHubCallbackAsync(_fake);
        var stolen = Query(await attacker.GitHubCallbackAsync(_fake), "code");

        Assert.Equal("/#failed", await victim.FollowAsync(WithQuery(victims, "code", stolen)));

        Assert.False((await victim.SessionAsync()).Authenticated);
        Assert.Equal(0, await IdentitiesAsync("github", id.ToString()));
    }

    // ── Google ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An id token that fails any one check is refused before anybody is identified: no session and no
    /// account, although the identity it names is one the operator has admitted.
    /// </summary>
    [Theory]
    [InlineData(IdTokenFault.WrongSignature)]
    [InlineData(IdTokenFault.WrongAudience)]
    [InlineData(IdTokenFault.WrongIssuer)]
    [InlineData(IdTokenFault.Expired)]
    [InlineData(IdTokenFault.WrongNonce)]
    public async Task A_google_token_with_a_wrong_signature_is_refused(IdTokenFault fault)
    {
        await using var gateway = Gateway();
        var sub = NewGoogleSub();
        await ApproveAsync($"google:{sub}");
        _fake.GoogleUser = new GoogleAccount(sub, "Ann", "ann@example.com");
        _fake.Fault = fault;

        using var browser = new Browser(gateway);
        Assert.Equal("/#failed", await browser.SignInWithGoogleAsync(_fake));

        Assert.False((await browser.SessionAsync()).Authenticated);
        Assert.Equal(0, await IdentitiesAsync("google", sub));
    }

    /// <summary>The same token, unspoilt, does sign in: the theory above fails for its fault and nothing else.</summary>
    [Fact]
    public async Task A_good_google_token_signs_in()
    {
        await using var gateway = Gateway();
        var sub = NewGoogleSub();
        await ApproveAsync($"google:{sub}");
        _fake.GoogleUser = new GoogleAccount(sub, "Ann", "ann@example.com");

        using var browser = new Browser(gateway);
        Assert.Equal("/", await browser.SignInWithGoogleAsync(_fake));

        Assert.Equal("Ann", (await browser.SessionAsync()).User!.DisplayName);
        Assert.Equal(1, await IdentitiesAsync("google", sub));
        Assert.False(browser.Has(ExternalSignIn.CookieName));
    }

    /// <summary>
    /// The <c>sub</c> is the identity; an email is only what the person is called this week. A changed
    /// address signs in to the same account.
    /// </summary>
    [Fact]
    public async Task A_changed_google_email_keeps_the_account()
    {
        await using var gateway = Gateway();
        var sub = NewGoogleSub();
        await ApproveAsync($"google:{sub}");

        _fake.GoogleUser = new GoogleAccount(sub, "Ann", "ann@example.com");
        using var first = new Browser(gateway);
        Assert.Equal("/", await first.SignInWithGoogleAsync(_fake));

        _fake.GoogleUser = new GoogleAccount(sub, "Ann", "ann.other@example.org");
        using var second = new Browser(gateway);
        Assert.Equal("/", await second.SignInWithGoogleAsync(_fake));

        Assert.Equal((await first.SessionAsync()).User!.Id, (await second.SessionAsync()).User!.Id);
        Assert.Equal(1, await IdentitiesAsync("google", sub));
    }

    // ── the admission list, through the providers ───────────────────────────

    /// <summary>
    /// Signing in with a provider goes through the admission list, like every other way in. An identity
    /// nobody admitted is told it is waiting, and one the operator turned away that it is refused; neither
    /// gets an account or a session. Shown red by completing the sign-in with the provisioning call that
    /// skips the list, which hands an account to anyone with a GitHub login.
    /// </summary>
    [Theory]
    [InlineData(null, "/#waiting")]
    [InlineData("refuse", "/#refused")]
    public async Task An_identity_the_operator_has_not_admitted_gets_no_session(string? decision, string expected)
    {
        await using var gateway = Gateway();
        var id = NewGitHubId();
        if (decision is not null)
        {
            Assert.Equal(0, await AdminCommands.RunAsync([decision, $"github:{id}"], Db, TextWriter.Null));
        }

        _fake.GitHubUser = new GitHubAccount(id, "stranger");
        using var browser = new Browser(gateway);

        Assert.Equal(expected, await browser.SignInWithGitHubAsync(_fake));
        Assert.False((await browser.SessionAsync()).Authenticated);
        Assert.Equal(0, await IdentitiesAsync("github", id.ToString()));
        Assert.False(browser.Has(ExternalSignIn.CookieName));
    }

    /// <summary>A disabled account is told so, and gets no session.</summary>
    [Fact]
    public async Task A_disabled_account_is_told_so_and_gets_no_session()
    {
        await using var gateway = Gateway();
        var id = NewGitHubId();
        await ApproveAsync($"github:{id}");
        _fake.GitHubUser = new GitHubAccount(id, "octocat");

        using var first = new Browser(gateway);
        Assert.Equal("/", await first.SignInWithGitHubAsync(_fake));
        var userId = (await first.SessionAsync()).User!.Id;
        Assert.Equal(0, await AdminCommands.RunAsync(["disable", userId], Db, TextWriter.Null));

        using var second = new Browser(gateway);
        Assert.Equal("/#disabled", await second.SignInWithGitHubAsync(_fake));
        Assert.False((await second.SessionAsync()).Authenticated);
    }

    // ── configuration ───────────────────────────────────────────────────────

    /// <summary>
    /// Only what is configured is offered and reachable: an unconfigured provider is neither listed nor
    /// has a start or a callback, rather than having one that fails when somebody tries it.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_provider_is_not_offered()
    {
        await using var googleOnly = Gateway(github: false);
        using var browser = new Browser(googleOnly);

        Assert.Equal(["google"], (await browser.Http.GetFromJsonAsync<string[]>("/api/providers"))!);

        using (var start = await browser.Http.GetAsync("/auth/github/start"))
        {
            Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        }

        using (var callback = await browser.Http.GetAsync("/auth/github/callback?code=x&state=y"))
        {
            Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
        }

        using (var unknown = await browser.Http.GetAsync("/auth/elsewhere/start"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        await using var neither = Gateway(github: false, google: false);
        using var other = new Browser(neither);
        Assert.Empty((await other.Http.GetFromJsonAsync<string[]>("/api/providers"))!);
    }

    /// <summary>
    /// A provider with an id and no secret, or a secret and no id, is a mistake in the environment file,
    /// and the gateway does not start: starting would offer a sign-in that fails for everybody, or quietly
    /// not offer one the operator meant to.
    /// </summary>
    [Theory]
    [InlineData("ENACTIVE_GITHUB_CLIENT_ID", "ENACTIVE_GITHUB_CLIENT_SECRET")]
    [InlineData("ENACTIVE_GITHUB_CLIENT_SECRET", "ENACTIVE_GITHUB_CLIENT_ID")]
    [InlineData("ENACTIVE_GOOGLE_CLIENT_ID", "ENACTIVE_GOOGLE_CLIENT_SECRET")]
    [InlineData("ENACTIVE_GOOGLE_CLIENT_SECRET", "ENACTIVE_GOOGLE_CLIENT_ID")]
    public async Task Half_configured_provider_refuses_to_start(string present, string missing)
    {
        await using var gateway = TestGateway.Create(database, devSignIn: false, builder =>
        {
            builder.UseSetting(ExternalProviders.PublicOriginSetting, "https://remote.example.test");
            builder.UseSetting(present, "value");
        });

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(missing, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The public origin is what the providers are told to send people back to, so it has to be there and
    /// has to be https: a callback over plain http would carry the code past anyone on the path. Plain http
    /// is allowed for this machine only, for a developer's own sign-in.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("remote.example.test")]
    [InlineData("http://remote.example.test")]
    [InlineData("https://remote.example.test/panel")]
    [InlineData("https://remote.example.test/?next=/")]
    public async Task A_provider_without_a_proper_public_origin_refuses_to_start(string? origin)
    {
        await using var gateway = Gateway(configure: builder =>
            builder.UseSetting(ExternalProviders.PublicOriginSetting, origin ?? ""));

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(ExternalProviders.PublicOriginSetting, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A developer's own machine may use http, and only it.</summary>
    [Theory]
    [InlineData("http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    public async Task A_loopback_http_origin_starts(string origin)
    {
        await using var gateway = Gateway(configure: builder =>
            builder.UseSetting(ExternalProviders.PublicOriginSetting, origin));

        using var response = await gateway.CreateClient().GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// The callback the providers are given is on the public origin, whatever the request came in as.
    /// Behind the tunnel every request arrives as plain http from 127.0.0.1, and the provider would
    /// refuse a callback that is not the exact registered one; and a Host header is the caller's to
    /// write, so building the callback from it would let a caller choose where the code is sent.
    /// </summary>
    [Theory]
    [InlineData("github")]
    [InlineData("google")]
    public async Task The_callback_given_to_the_provider_is_on_the_public_origin(string provider)
    {
        await using var gateway = Gateway(configure: builder =>
            builder.UseSetting(ExternalProviders.PublicOriginSetting, "https://remote.example.test"));
        using var http = gateway.CreateDefaultClient(new Uri("http://127.0.0.1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/{provider}/start");
        request.Headers.Host = "attacker.example";

        using var start = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal($"https://remote.example.test/auth/{provider}/callback",
            Query(start.Headers.Location!, "redirect_uri"));
    }

    // ── logging ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The callback's query string carries the authorization code, and nothing of it may reach a log -
    /// even with every category at Trace, as an operator chasing a fault might set it. Two wrote it before
    /// they were capped: the request log, which prints each request's full URL, and the handlers' debug
    /// output, which prints the authorization request and the callback message.
    /// </summary>
    [Fact]
    public async Task Nothing_the_providers_send_back_is_logged()
    {
        var logs = new CapturingLoggerProvider();
        await using var gateway = Gateway(configure: builder =>
        {
            builder.UseSetting("Logging:LogLevel:Default", "Trace");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logs);
            });
        });

        var id = NewGitHubId();
        await ApproveAsync($"github:{id}");
        _fake.GitHubUser = new GitHubAccount(id, "octocat");
        var sub = NewGoogleSub();
        await ApproveAsync($"google:{sub}");
        _fake.GoogleUser = new GoogleAccount(sub, "Ann", "ann@example.com");

        using var github = new Browser(gateway);
        var callback = await github.GitHubCallbackAsync(_fake);
        Assert.Equal("/", await github.FollowAsync(callback));
        using var google = new Browser(gateway);
        Assert.Equal("/", await google.SignInWithGoogleAsync(_fake));

        Assert.NotEmpty(logs.Lines);
        var secrets = _fake.Secrets.Append(Query(callback, "state")).ToArray();
        Assert.All(logs.Lines, line => Assert.DoesNotContain(secrets, line.Contains));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> Gateway(
        bool github = true, bool google = true, Action<IWebHostBuilder>? configure = null)
        => TestGateway.Create(database, devSignIn: false, builder =>
        {
            builder.UseSetting(ExternalProviders.PublicOriginSetting, Origin.ToString().TrimEnd('/'));

            if (github)
            {
                builder.UseSetting("ENACTIVE_GITHUB_CLIENT_ID", FakeProviders.GitHubClientId);
                builder.UseSetting("ENACTIVE_GITHUB_CLIENT_SECRET", FakeProviders.GitHubClientSecret);
                builder.UseSetting("ENACTIVE_GITHUB_BASE", FakeProviders.GitHubBase);
                builder.UseSetting("ENACTIVE_GITHUB_API", FakeProviders.GitHubApi);
            }

            if (google)
            {
                builder.UseSetting("ENACTIVE_GOOGLE_CLIENT_ID", FakeProviders.GoogleClientId);
                builder.UseSetting("ENACTIVE_GOOGLE_CLIENT_SECRET", FakeProviders.GoogleClientSecret);
                builder.UseSetting("ENACTIVE_GOOGLE_AUTHORITY", FakeProviders.GoogleAuthority);
            }

            // Configure, not PostConfigure: the handlers' own post-configuration turns the handler into
            // the HttpClient they use (and, for Google, the discovery client), so a handler set after it
            // would be ignored and the gateway would go looking for the real providers.
            builder.ConfigureTestServices(services =>
            {
                services.Configure<OAuthOptions>(ExternalSignIn.GitHub, o => o.BackchannelHttpHandler = _fake.CreateHandler());
                services.Configure<OpenIdConnectOptions>(ExternalSignIn.Google, o => o.BackchannelHttpHandler = _fake.CreateHandler());
            });

            configure?.Invoke(builder);
        });

    private async Task ApproveAsync(string identity)
        => Assert.Equal(0, await AdminCommands.RunAsync(["approve", identity], Db, TextWriter.Null));

    private Task<long> IdentitiesAsync(string provider, string subject)
        => database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM external_identities WHERE provider = '{provider}' AND subject = '{subject}'");

    private static long NewGitHubId() => Random.Shared.NextInt64(1, long.MaxValue);

    private static string NewGoogleSub() => "1" + Random.Shared.NextInt64(1, long.MaxValue).ToString("D20");

    private static string Query(Uri uri, string name)
        => QueryHelpers.ParseQuery(uri.Query)[name].ToString();

    private static Uri WithQuery(Uri uri, string name, string value)
    {
        var query = QueryHelpers.ParseQuery(uri.Query)
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
        query[name] = value;
        return new Uri(QueryHelpers.AddQueryString(uri.GetLeftPart(UriPartial.Path), query));
    }

    /// <summary>
    /// A browser of the gateway: its own cookie jar, and no redirect followed for it, so each hop is
    /// seen and a test can tamper with the one it is about.
    /// </summary>
    private sealed class Browser : IDisposable
    {
        private readonly CookieContainer _cookies = new();

        public Browser(WebApplicationFactory<Program> gateway)
        {
            Http = gateway.CreateDefaultClient(Origin, new CookieContainerHandler(_cookies));
        }

        public HttpClient Http { get; }

        public bool Has(string cookie) => _cookies.GetCookies(Origin).Any(c => c.Name == cookie);

        public async Task<SessionView> SessionAsync()
            => (await Http.GetFromJsonAsync<SessionView>("/api/session", RemoteJson.Options))!;

        /// <summary>The whole GitHub sign-in; where the gateway sent the browser at the end.</summary>
        public async Task<string> SignInWithGitHubAsync(FakeProviders fake)
            => await FollowAsync(await GitHubCallbackAsync(fake));

        /// <summary>Start, and GitHub's answer: the callback the browser is sent to, not yet visited.</summary>
        public async Task<Uri> GitHubCallbackAsync(FakeProviders fake)
        {
            var authorize = await RedirectAsync(Http, new Uri("/auth/github/start", UriKind.Relative));
            using var provider = fake.CreateBrowser();
            return await RedirectAsync(provider, authorize);
        }

        /// <summary>The callback, then wherever it leads; where the browser lands.</summary>
        public async Task<string> FollowAsync(Uri callback) => await LandAsync(await RedirectAsync(Http, callback));

        /// <summary>The whole Google sign-in, through the form Google's page posts back.</summary>
        public async Task<string> SignInWithGoogleAsync(FakeProviders fake)
        {
            var authorize = await RedirectAsync(Http, new Uri("/auth/google/start", UriKind.Relative));

            using var provider = fake.CreateBrowser();
            using var page = await provider.GetAsync(authorize);
            page.EnsureSuccessStatusCode();
            var (action, fields) = FakeProviders.ReadFormPost(await page.Content.ReadAsStringAsync());

            using var posted = await Http.PostAsync(action, new FormUrlEncodedContent(fields));
            return await LandAsync(Location(posted));
        }

        public void Dispose() => Http.Dispose();

        /// <summary>
        /// Through <c>/auth/complete</c> when the callback was accepted; a refused callback goes straight
        /// to the panel.
        /// </summary>
        private async Task<string> LandAsync(Uri next)
            => next.OriginalString == "/auth/complete"
                ? (await RedirectAsync(Http, next)).OriginalString
                : next.OriginalString;

        private static async Task<Uri> RedirectAsync(HttpClient client, Uri uri)
        {
            using var response = await client.GetAsync(uri);
            return Location(response);
        }

        private static Uri Location(HttpResponseMessage response)
            => response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                ? response.Headers.Location!
                : throw new InvalidOperationException(
                    $"Expected a redirect from {response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}.");
    }

    /// <summary>Every log line, as written, from every category.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => provider.Lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
