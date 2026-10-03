namespace Enactive.Remote.Gateway.Tests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    // Every gateway of a test logs into this, at Trace, so any test can check it wrote no secret.
    private readonly CapturingLoggerProvider _logs = new();

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
    /// every time, so only the state check stands between it and a session - and it stands before the
    /// code is redeemed at all.
    /// </summary>
    [Theory]
    [InlineData("github", "garbage")]
    [InlineData("github", "another browser's")]
    [InlineData("google", "garbage")]
    [InlineData("google", "another browser's")]
    public async Task A_forged_state_creates_no_session(string provider, string forgery)
    {
        await using var gateway = Gateway();
        var subject = Admit(provider, "mallory");
        await ApproveAsync($"{provider}:{subject}");

        using var victim = new Browser(gateway);
        using var attacker = new Browser(gateway);
        var answer = await victim.AnswerAsync(provider, _fake);
        var forged = forgery == "garbage"
            ? "forged-state"
            : (await attacker.AnswerAsync(provider, _fake)).Fields["state"];

        Assert.Equal("/#failed", await victim.DeliverAsync(answer.With("state", forged)));

        Assert.Equal(0, _fake.TokenRequests);
        Assert.False((await victim.SessionAsync()).Authenticated);
        Assert.Equal(0, await IdentitiesAsync(provider, subject));
        AssertNoSecretLogged(answer.Fields["state"], forged);
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
        var id = Admit(ExternalSignIn.GitHub, "mallory");
        await ApproveAsync($"github:{id}");

        using var victim = new Browser(gateway);
        using var attacker = new Browser(gateway);
        var victims = await victim.AnswerAsync(ExternalSignIn.GitHub, _fake);
        var stolen = (await attacker.AnswerAsync(ExternalSignIn.GitHub, _fake)).Fields["code"];

        Assert.Equal("/#failed", await victim.DeliverAsync(victims.With("code", stolen)));

        Assert.False((await victim.SessionAsync()).Authenticated);
        Assert.Equal(0, await IdentitiesAsync("github", id));
        AssertNoSecretLogged(victims.Fields["state"]);
    }

    /// <summary>
    /// A callback is limited per caller, before the handler runs. Every callback whose state passes makes
    /// the gateway post this service's client secret to the provider's token endpoint, and nothing on the
    /// server makes a state single-use: a client that keeps its own state and correlation cookie can
    /// replay them with made-up codes as fast as it likes, spending this client's standing with GitHub or
    /// Google until they block everybody's sign-in. The callbacks are answered inside authentication, so
    /// the limit on the sign-in endpoints never saw them.
    /// </summary>
    [Fact]
    public async Task Replaying_one_state_at_the_callback_is_limited_before_any_code_is_redeemed()
    {
        await using var gateway = Gateway();
        using var browser = new Browser(gateway);
        var answer = await browser.AnswerAsync(ExternalSignIn.GitHub, _fake);
        var kept = browser.CookieHeader(answer.Target);

        // No cookie jar: the kept cookie is sent every time, as a replaying client would send it, although
        // the gateway asks for it to be deleted after the first use.
        using var replayer = gateway.CreateDefaultClient(Origin);
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt <= ExternalSignIn.CallbacksPerMinute; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, answer.With("code", $"made-up-{attempt}").CallbackUri);
            request.Headers.Add("Cookie", kept);
            using var response = await replayer.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses[..^1], status => Assert.Equal(HttpStatusCode.Redirect, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(ExternalSignIn.CallbacksPerMinute, _fake.TokenRequests);
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
        AssertNoSecretLogged();
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

    /// <summary>
    /// <c>/auth/complete</c> believes only an External cookie this gateway issued, unaltered and in its
    /// ten minutes. Without one - a direct visit, a cookie somebody edited, one kept past its time - it
    /// signs nobody in, though the identity it names is admitted. The genuine cookie is the control.
    /// </summary>
    [Theory]
    [InlineData("none", "/#failed")]
    [InlineData("tampered", "/#failed")]
    [InlineData("expired", "/#failed")]
    [InlineData("genuine", "/")]
    public async Task Complete_signs_in_only_on_a_genuine_unexpired_answer(string cookie, string expected)
    {
        await using var gateway = Gateway();
        var id = NewGitHubId().ToString(CultureInfo.InvariantCulture);
        await ApproveAsync($"github:{id}");
        using var browser = new Browser(gateway);

        if (cookie != "none")
        {
            var expires = DateTimeOffset.UtcNow.AddMinutes(cookie == "expired" ? -1 : 10);
            var value = Ticket(gateway, AnswerFor(id), expires);

            browser.SetCookie(ExternalSignIn.CookieName, cookie == "tampered" ? Tamper(value) : value, "/auth");
        }

        using var response = await browser.Http.GetAsync("/auth/complete");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.OriginalString);
        Assert.Equal(expected == "/", (await browser.SessionAsync()).Authenticated);
        Assert.Equal(expected == "/" ? 1 : 0, await IdentitiesAsync("github", id));
    }

    // ── one sign-in per answer ──────────────────────────────────────────────

    /// <summary>
    /// The answer is good for one sign-in. Deleting the browser's External cookie does not end it: the ticket is
    /// stateless and stays acceptable for its ten minutes, so a copy kept by anybody - a second browser here -
    /// was redeemed into a second session without the provider being asked again. The redemption is recorded,
    /// and the second one finds it.
    /// </summary>
    [Fact]
    public async Task A_redeemed_sign_in_ticket_cannot_be_redeemed_again()
    {
        await using var gateway = Gateway();
        var id = NewGitHubId().ToString(CultureInfo.InvariantCulture);
        await ApproveAsync($"github:{id}");
        var kept = Ticket(gateway, AnswerFor(id));

        using var first = new Browser(gateway);
        Assert.Equal("/", await RedeemAsync(first, kept));
        Assert.False(first.Has(ExternalSignIn.CookieName));

        using var replayer = new Browser(gateway);
        Assert.Equal("/#failed", await RedeemAsync(replayer, kept));

        Assert.False((await replayer.SessionAsync()).Authenticated);
        Assert.Equal(1, await SessionsAsync(id));
    }

    /// <summary>
    /// Redeemed by several browsers at the same moment - a double-click, or a replay racing the real one - the
    /// answer still makes one session. A check made first and a write after it would let every one of them
    /// through; the database's key on the answer's id, written in the transaction that opens the session, is
    /// what decides. A first sign-in, so the account is being made by the same racing requests.
    /// </summary>
    [Fact]
    public async Task Two_redemptions_of_one_ticket_at_once_make_one_session()
    {
        await using var gateway = Gateway();
        var id = NewGitHubId().ToString(CultureInfo.InvariantCulture);
        await ApproveAsync($"github:{id}");
        var kept = Ticket(gateway, AnswerFor(id));

        var browsers = Enumerable.Range(0, 5).Select(_ => new Browser(gateway)).ToArray();
        try
        {
            var landed = await Task.WhenAll(browsers.Select(browser => RedeemAsync(browser, kept)));

            Assert.Single(landed, place => place == "/");
            Assert.All(landed.Where(place => place != "/"), place => Assert.Equal("/#failed", place));
            Assert.Equal(1, await SessionsAsync(id));
        }
        finally
        {
            foreach (var browser in browsers)
            {
                browser.Dispose();
            }
        }
    }

    /// <summary>
    /// The issue's reproduction. An answer issued before the account's sessions were all ended - by the person
    /// signing out everywhere, by the operator's revoke, by a disablement since lifted - opens no session after
    /// it. Signing out everywhere is what a person does when a copy of their sign-in may be in somebody else's
    /// hands, and the version it moves on cannot catch this: the answer names no account and no version, and
    /// the session it opened was stamped with the new one. An answer issued after the revocation still signs in.
    /// </summary>
    [Theory]
    [InlineData("everywhere")]
    [InlineData("operator")]
    [InlineData("disabled")]
    public async Task A_ticket_from_before_sign_out_everywhere_makes_no_session_after_it(string revocation)
    {
        await using var gateway = Gateway();
        var id = NewGitHubId().ToString(CultureInfo.InvariantCulture);
        await ApproveAsync($"github:{id}");

        using var first = new Browser(gateway);
        Assert.Equal("/", await RedeemAsync(first, Ticket(gateway, AnswerFor(id))));
        var userId = (await first.SessionAsync()).User!.Id;
        var kept = Ticket(gateway, AnswerFor(id));

        switch (revocation)
        {
            case "everywhere":
                // What POST /api/logout-all calls for the signed-in person.
                await new SessionStore(Db, TimeProvider.System).RevokeAllAsync(userId, default);
                break;
            case "operator":
                Assert.Equal(0, await AdminCommands.RunAsync(["sessions", "revoke", userId], Db, TextWriter.Null));
                break;
            default:
                Assert.Equal(0, await AdminCommands.RunAsync(["disable", userId], Db, TextWriter.Null));
                Assert.Equal(0, await AdminCommands.RunAsync(["enable", userId], Db, TextWriter.Null));
                break;
        }

        using var replayer = new Browser(gateway);
        Assert.Equal("/#failed", await RedeemAsync(replayer, kept));
        Assert.False((await replayer.SessionAsync()).Authenticated);
        Assert.Equal(0, await SessionsAsync(id, open: true));

        // Issued a moment after the revocation, as the person's own sign-in after it is.
        var revokedAt = new DateTimeOffset(DateTime.SpecifyKind(
            (DateTime)(await database.ScalarAsync($"SELECT sessions_revoked_at FROM users WHERE id = '{userId}'"))!,
            DateTimeKind.Utc));
        using var later = new Browser(gateway);
        Assert.Equal("/", await RedeemAsync(later, Ticket(gateway, AnswerFor(id, issued: revokedAt.AddMilliseconds(1)))));
        Assert.Equal(userId, (await later.SessionAsync()).User!.Id);
    }

    /// <summary>
    /// An answer without its id cannot be recorded as redeemed, and one without the moment it was made cannot be
    /// compared with a revocation; either would be good for any number of sign-ins at any time. Refused, though
    /// the identity it names is admitted. Only the gateway can make such a cookie, so this is the check that
    /// none it ever made is believed.
    /// </summary>
    [Theory]
    [InlineData(ExternalSignIn.TicketClaim)]
    [InlineData(ExternalSignIn.IssuedClaim)]
    public async Task A_ticket_without_an_id_is_refused(string missing)
    {
        await using var gateway = Gateway();
        var id = NewGitHubId().ToString(CultureInfo.InvariantCulture);
        await ApproveAsync($"github:{id}");
        var answer = AnswerFor(id);
        var without = new ClaimsPrincipal(new ClaimsIdentity(
            answer.Claims.Where(claim => claim.Type != missing), ExternalSignIn.GitHub));

        using var browser = new Browser(gateway);
        Assert.Equal("/#failed", await RedeemAsync(browser, Ticket(gateway, without)));

        Assert.False((await browser.SessionAsync()).Authenticated);
        Assert.Equal(0, await IdentitiesAsync("github", id));
    }

    /// <summary>
    /// The control for the tests above: a first sign-in with a fresh answer makes the account and its session,
    /// and records the answer as redeemed under its id.
    /// </summary>
    [Fact]
    public async Task A_normal_first_redemption_still_signs_in()
    {
        await using var gateway = Gateway();
        var id = NewGitHubId().ToString(CultureInfo.InvariantCulture);
        await ApproveAsync($"github:{id}");
        var ticket = NewTicketId();

        using var browser = new Browser(gateway);
        Assert.Equal("/", await RedeemAsync(browser, Ticket(gateway, AnswerFor(id, ticket))));

        Assert.Equal("octocat", (await browser.SessionAsync()).User!.DisplayName);
        Assert.Equal(1, await SessionsAsync(id));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM signin_redemptions WHERE id = '{ticket}'"));
    }

    /// <summary>
    /// A record of a redemption is needed only while its answer could still be presented, ten minutes; every
    /// sign-in writes one, so kept for good the table would grow with every sign-in there ever was. The hourly
    /// pass removes those older than a day, and leaves a recent one alone.
    /// </summary>
    [Fact]
    public async Task Old_redemption_records_are_removed()
    {
        var old = NewTicketId();
        var recent = NewTicketId();
        await database.ExecuteAsync(
            $"""
            INSERT INTO signin_redemptions (id, redeemed_at)
            VALUES ('{old}', UTC_TIMESTAMP(3) - INTERVAL 25 HOUR), ('{recent}', UTC_TIMESTAMP(3) - INTERVAL 1 HOUR)
            """);

        await new Services.Retention(Db, Services.Retention.DefaultDays).TrimAsync();

        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM signin_redemptions WHERE id = '{old}'"));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM signin_redemptions WHERE id = '{recent}'"));
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
    /// The provider endpoints move only for tests. Anywhere else one stray line would replace Google's
    /// discovery document and signing keys - the trust anchor for every id token - or GitHub's token and
    /// user endpoints, and whoever ran the host it named could sign in as any admitted person.
    /// </summary>
    [Theory]
    [InlineData("ENACTIVE_GITHUB_BASE")]
    [InlineData("ENACTIVE_GITHUB_API")]
    [InlineData("ENACTIVE_GOOGLE_AUTHORITY")]
    public async Task A_provider_endpoint_override_refuses_to_start_outside_development(string setting)
    {
        await using var gateway = TestGateway.Create(database, devSignIn: false, builder =>
        {
            builder.UseSetting("environment", "Production");
            builder.UseSetting(setting, "https://elsewhere.example");
        });

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(setting, refused.Message, StringComparison.Ordinal);
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
    /// they were capped: the request log, which prints each request's full URL at the default level, and
    /// the handlers' debug output, which prints the authorization request and the callback message. The
    /// refused flows (forged state, stolen code, bad id token) are checked the same way in their tests.
    /// </summary>
    [Fact]
    public async Task Nothing_the_providers_send_back_is_logged()
    {
        await using var gateway = Gateway();
        var id = Admit(ExternalSignIn.GitHub, "octocat");
        await ApproveAsync($"github:{id}");

        using var github = new Browser(gateway);
        var answer = await github.AnswerAsync(ExternalSignIn.GitHub, _fake);
        Assert.Equal("/", await github.DeliverAsync(answer));

        var sub = Admit(ExternalSignIn.Google, "Ann");
        await ApproveAsync($"google:{sub}");
        using var google = new Browser(gateway);
        Assert.Equal("/", await google.SignInWithGoogleAsync(_fake));

        AssertNoSecretLogged(answer.Fields["state"]);
    }

    /// <summary>
    /// A sign-in is written to the person's security log as the provider it came through and nothing else:
    /// not the provider's subject, not the name it reports, not the address the browser came from. The log
    /// is shown to whoever holds a session of the account, and the row is the whole of what it says.
    /// </summary>
    [Theory]
    [InlineData("github")]
    [InlineData("google")]
    public async Task A_sign_in_writes_one_audit_row_naming_only_the_provider(string provider)
    {
        await using var gateway = Gateway();
        var subject = Admit(provider, "octocat");
        await ApproveAsync($"{provider}:{subject}");
        var before = await database.ScalarLongAsync("SELECT IFNULL(MAX(id), 0) FROM audit");

        using var browser = new Browser(gateway);
        Assert.Equal("/", await browser.DeliverAsync(await browser.AnswerAsync(provider, _fake)));
        var userId = (await browser.SessionAsync()).User!.Id;

        Assert.Equal(
            [$"{userId}|user:{userId}|signin.{provider}|-"],
            await database.StringsAsync(
                "SELECT CONCAT_WS('|', owner_id, actor, action, IFNULL(target, '-')) FROM audit "
                + $"WHERE id > {before} ORDER BY id"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> Gateway(
        bool github = true, bool google = true, Action<IWebHostBuilder>? configure = null)
        => TestGateway.Create(database, devSignIn: false, builder =>
        {
            builder.UseSetting(ExternalProviders.PublicOriginSetting, Origin.ToString().TrimEnd('/'));

            builder.UseSetting("Logging:LogLevel:Default", "Trace");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(_logs);
            });

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

    /// <summary>The sessions of the account of GitHub identity <paramref name="id"/>; only unrevoked ones if <paramref name="open"/>.</summary>
    private Task<long> SessionsAsync(string id, bool open = false)
        => database.ScalarLongAsync(
            $"""
            SELECT COUNT(*) FROM user_sessions s JOIN external_identities i ON i.user_id = s.user_id
            WHERE i.provider = 'github' AND i.subject = '{id}' {(open ? "AND s.revoked_at IS NULL" : "")}
            """);

    private static string NewTicketId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>GitHub's answer for <paramref name="id"/>, as its handler makes it: a fresh id, issued now.</summary>
    private static ClaimsPrincipal AnswerFor(string id, string? ticket = null, DateTimeOffset? issued = null)
        => ExternalSignIn.Answer(
            ExternalSignIn.GitHub, id, "octocat", ticket ?? NewTicketId(), issued ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// The External cookie's value for <paramref name="answer"/>, protected with the gateway's own ticket format:
    /// what the cookie handler writes at the end of a provider's callback, and what a person who kept a copy of
    /// that cookie holds.
    /// </summary>
    private static string Ticket(
        WebApplicationFactory<Program> gateway, ClaimsPrincipal answer, DateTimeOffset? expires = null)
        => gateway.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ExternalSignIn.SchemeName).TicketDataFormat
            .Protect(new AuthenticationTicket(
                answer,
                new AuthenticationProperties { ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddMinutes(10) },
                ExternalSignIn.SchemeName));

    /// <summary>The cookie value delivered to <c>/auth/complete</c> by <paramref name="browser"/>; where it was sent.</summary>
    private static async Task<string> RedeemAsync(Browser browser, string ticket)
    {
        browser.SetCookie(ExternalSignIn.CookieName, ticket, "/auth");
        using var response = await browser.Http.GetAsync("/auth/complete");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private static long NewGitHubId() => Random.Shared.NextInt64(1, long.MaxValue);

    private static string NewGoogleSub() => "1" + Random.Shared.NextInt64(1, long.MaxValue).ToString("D20");

    /// <summary>A fresh identity of <paramref name="provider"/>, as the fake will report it next; its subject.</summary>
    private string Admit(string provider, string name)
    {
        if (provider == ExternalSignIn.GitHub)
        {
            var id = NewGitHubId();
            _fake.GitHubUser = new GitHubAccount(id, name);
            return id.ToString(CultureInfo.InvariantCulture);
        }

        var sub = NewGoogleSub();
        _fake.GoogleUser = new GoogleAccount(sub, name, $"{name}@example.com");
        return sub;
    }

    /// <summary>
    /// No log line holds a code or token the fake issued, or any of <paramref name="more"/>. Not empty
    /// either, so a test whose logging was never wired up cannot pass by having seen nothing.
    /// </summary>
    private void AssertNoSecretLogged(params string[] more)
    {
        var secrets = _fake.Secrets.Concat(more).ToArray();
        Assert.NotEmpty(_logs.Lines);
        Assert.All(_logs.Lines, line => Assert.DoesNotContain(secrets, line.Contains));
    }

    private static string Query(Uri uri, string name)
        => QueryHelpers.ParseQuery(uri.Query)[name].ToString();

    /// <summary>The same protected value with one character changed in its middle.</summary>
    private static string Tamper(string value)
    {
        var characters = value.ToCharArray();
        var middle = characters.Length / 2;
        characters[middle] = characters[middle] == 'A' ? 'B' : 'A';
        return new string(characters);
    }

    /// <summary>
    /// What a provider sends the browser back to the gateway with: GitHub's redirect to the callback, or
    /// the form Google's page posts to it. Its fields are what a test tampers with.
    /// </summary>
    private sealed record ProviderAnswer(Uri Target, IReadOnlyDictionary<string, string> Fields, bool Posted)
    {
        public Uri CallbackUri => Posted
            ? Target
            : new Uri(QueryHelpers.AddQueryString(
                Target.ToString(), Fields.ToDictionary(f => f.Key, f => (string?)f.Value)));

        public ProviderAnswer With(string name, string value)
            => this with { Fields = new Dictionary<string, string>(Fields) { [name] = value } };
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

        public string CookieHeader(Uri uri) => _cookies.GetCookieHeader(uri);

        public void SetCookie(string name, string value, string path)
            => _cookies.Add(new Cookie(name, value, path, Origin.Host));

        /// <summary>The whole GitHub sign-in; where the gateway sent the browser at the end.</summary>
        public async Task<string> SignInWithGitHubAsync(FakeProviders fake)
            => await DeliverAsync(await AnswerAsync(ExternalSignIn.GitHub, fake));

        /// <summary>The whole Google sign-in, through the form Google's page posts back.</summary>
        public async Task<string> SignInWithGoogleAsync(FakeProviders fake)
            => await DeliverAsync(await AnswerAsync(ExternalSignIn.Google, fake));

        /// <summary>Start, and the provider's answer, not yet delivered to the gateway.</summary>
        public async Task<ProviderAnswer> AnswerAsync(string provider, FakeProviders fake)
        {
            var authorize = await RedirectAsync(Http, new Uri($"/auth/{provider}/start", UriKind.Relative));
            using var client = fake.CreateBrowser();

            if (provider == ExternalSignIn.GitHub)
            {
                var callback = await RedirectAsync(client, authorize);
                return new ProviderAnswer(
                    new Uri(callback.GetLeftPart(UriPartial.Path)),
                    QueryHelpers.ParseQuery(callback.Query).ToDictionary(p => p.Key, p => p.Value.ToString()),
                    Posted: false);
            }

            using var page = await client.GetAsync(authorize);
            page.EnsureSuccessStatusCode();
            var (action, fields) = FakeProviders.ReadFormPost(await page.Content.ReadAsStringAsync());
            return new ProviderAnswer(action, fields, Posted: true);
        }

        /// <summary>The answer delivered to the callback, then wherever it leads; where the browser lands.</summary>
        public async Task<string> DeliverAsync(ProviderAnswer answer)
        {
            using var response = answer.Posted
                ? await Http.PostAsync(answer.Target, new FormUrlEncodedContent(answer.Fields))
                : await Http.GetAsync(answer.CallbackUri);

            return await LandAsync(Location(response));
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
