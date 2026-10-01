namespace Enactive.Remote.Gateway.Accounts;

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

/// <summary>One provider's client registration: the id and the secret it was issued.</summary>
public sealed record ProviderClient(string Id, string Secret);

/// <summary>
/// Which providers this gateway offers, read once at start-up, and where they send people back to.
///
/// <para>A provider is offered when both its id and its secret are set, and not offered when neither
/// is. One without the other stops the start (<see cref="FromConfiguration"/>): it is a mistake in the
/// environment file, and a gateway that started anyway would either offer a sign-in that fails for
/// everybody or quietly not offer one the operator meant to.</para>
/// </summary>
public sealed record ExternalProviders(
    ProviderClient? GitHub, ProviderClient? Google, Uri? PublicOrigin,
    string GitHubBase, string GitHubApi, string GoogleAuthority)
{
    public const string PublicOriginSetting = "ENACTIVE_PUBLIC_ORIGIN";

    // Where the providers are, moved only by tests, which point them at a fake.
    private const string GitHubBaseSetting = "ENACTIVE_GITHUB_BASE";
    private const string GitHubApiSetting = "ENACTIVE_GITHUB_API";
    private const string GoogleAuthoritySetting = "ENACTIVE_GOOGLE_AUTHORITY";

    /// <summary>The providers offered, in the order the panel shows them.</summary>
    public IReadOnlyList<string> Names
        => new[] { GitHub is null ? null : ExternalSignIn.GitHub, Google is null ? null : ExternalSignIn.Google }
            .OfType<string>()
            .ToArray();

    public bool Offers(string provider) => Names.Contains(provider, StringComparer.Ordinal);

    /// <summary>
    /// The configured providers; throws on anything half-set or malformed, and on a provider endpoint
    /// moved outside Development.
    /// </summary>
    public static ExternalProviders FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        // The overrides exist for tests. Honoured in production, one stray line would replace Google's
        // discovery document and signing keys - what every id token is checked against - or GitHub's token
        // and user endpoints, and whoever ran the host it named could sign in as any admitted person. So,
        // like the development sign-in, set outside Development they stop the start.
        var moved = new[] { GitHubBaseSetting, GitHubApiSetting, GoogleAuthoritySetting }
            .Where(setting => !string.IsNullOrWhiteSpace(configuration[setting]))
            .ToArray();

        if (moved.Length > 0 && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{string.Join(", ", moved)} set in the {environment.EnvironmentName} environment. These move "
                + "the sign-in providers to another host, for tests, and are honoured only in Development: "
                + "anywhere else whoever runs that host could sign in as anybody.");
        }

        var github = Client(configuration, "ENACTIVE_GITHUB_CLIENT_ID", "ENACTIVE_GITHUB_CLIENT_SECRET");
        var google = Client(configuration, "ENACTIVE_GOOGLE_CLIENT_ID", "ENACTIVE_GOOGLE_CLIENT_SECRET");
        var origin = configuration[PublicOriginSetting];

        if ((github is not null || google is not null) && string.IsNullOrWhiteSpace(origin))
        {
            throw new InvalidOperationException(
                $"{PublicOriginSetting} is not set, and a sign-in provider is configured. The provider sends "
                + "people back to a callback on this origin, which must be the exact one registered with it: "
                + "set it to the gateway's public address, such as https://remote.enactive.dev.");
        }

        return new ExternalProviders(
            github, google,
            string.IsNullOrWhiteSpace(origin) ? null : Origin(origin),
            Endpoint(configuration, GitHubBaseSetting, "https://github.com"),
            Endpoint(configuration, GitHubApiSetting, "https://api.github.com"),
            Endpoint(configuration, GoogleAuthoritySetting, "https://accounts.google.com"));
    }

    private static ProviderClient? Client(IConfiguration configuration, string idSetting, string secretSetting)
    {
        var id = configuration[idSetting];
        var secret = configuration[secretSetting];

        return (string.IsNullOrWhiteSpace(id), string.IsNullOrWhiteSpace(secret)) switch
        {
            (true, true) => null,
            (false, false) => new ProviderClient(id!.Trim(), secret!.Trim()),
            (false, true) => throw Half(idSetting, secretSetting),
            (true, false) => throw Half(secretSetting, idSetting)
        };

        static InvalidOperationException Half(string present, string missing)
            => new($"{present} is set and {missing} is not. Set both to offer this sign-in, or neither to "
                   + "leave it out.");
    }

    /// <summary>
    /// The public origin, alone: scheme, host and port. https, because the callback carries the
    /// authorization code and plain http would hand it to anyone on the path; plain http only for this
    /// machine, which is where a developer signs in to their own gateway.
    /// </summary>
    private static Uri Origin(string value)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            && uri.PathAndQuery == "/" && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0)
        {
            return uri;
        }

        throw new InvalidOperationException(
            $"{PublicOriginSetting} is '{value}'. It must be the gateway's origin and nothing more, such as "
            + "https://remote.enactive.dev - https, or http only for localhost.");
    }

    private static string Endpoint(IConfiguration configuration, string setting, string fallback)
    {
        var value = configuration[setting];

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        // https, so a mistyped override cannot send a client secret or a code over plain http.
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{setting} is '{value}'; it must be an https URL.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

/// <summary>
/// Signing in with GitHub or Google.
///
/// <para>The provider's handler ends its round trip by putting who the provider says this is into the
/// <c>External</c> cookie, which only carries that answer the one hop to <c>/auth/complete</c> and lives
/// ten minutes. The session is ours, made there through the admission list, and nothing of the
/// provider's token is kept: identity is all this service asks a provider for.</para>
///
/// <para>Every place a sign-in ends is a fixed address on this site. None is taken from the request: a
/// return address a caller could set would make this an open redirect, sending somebody from a real
/// sign-in page to wherever a link said.</para>
/// </summary>
public static class ExternalSignIn
{
    public const string SchemeName = "External";
    public const string CookieName = "Enactive.External";

    /// <summary>The provider names: the schemes, the routes and the identity's provider column.</summary>
    public const string GitHub = "github";
    public const string Google = "google";

    private const string CompletePath = "/auth/complete";

    /// <summary>
    /// How many provider callbacks one caller may make in a minute. A person signing in makes one; ten
    /// leaves room for retries and a few people behind one address.
    /// </summary>
    public const int CallbacksPerMinute = 10;

    // The provider's answer, as the External cookie carries it.
    private const string ProviderClaim = "provider";
    private const string SubjectClaim = "subject";
    private const string DisplayClaim = "display";

    // Where a sign-in ends. The fragment is for the panel to read, and is never sent to a server, so it
    // reaches no log.
    private const string SignedInPage = "/";
    private const string WaitingPage = "/#waiting";
    private const string RefusedPage = "/#refused";
    private const string DisabledPage = "/#disabled";
    private const string FailedPage = "/#failed";

    /// <summary>The External cookie, and a handler for each configured provider and no other.</summary>
    /// <param name="secure">The User cookie's policy: Secure everywhere but Development.</param>
    public static AuthenticationBuilder AddExternalSignIn(
        this AuthenticationBuilder builder, ExternalProviders providers, CookieSecurePolicy secure)
    {
        builder.AddCookie(SchemeName, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = secure;

            // Lax: it is set on the callback, a navigation that arrived from the provider's site, and
            // read on the redirect straight after. Path /auth, because nothing else has any use for it.
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/auth";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            o.SlidingExpiration = false;
        });

        // Only what is configured is registered. A handler with an empty client id throws when it is
        // first built, and the authentication middleware builds every remote handler on every request.
        if (providers.GitHub is { } github)
        {
            builder.AddOAuth(GitHub, o =>
            {
                o.SignInScheme = SchemeName;
                o.ClientId = github.Id;
                o.ClientSecret = github.Secret;
                o.CallbackPath = $"/auth/{GitHub}/callback";
                o.AuthorizationEndpoint = $"{providers.GitHubBase}/login/oauth/authorize";
                o.TokenEndpoint = $"{providers.GitHubBase}/login/oauth/access_token";
                o.UserInformationEndpoint = $"{providers.GitHubApi}/user";
                o.UsePkce = true;
                o.SaveTokens = false;

                // Public profile only: no repository and no email access.
                o.Scope.Clear();

                o.Events.OnCreatingTicket = async context =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Authorization = new("Bearer", context.AccessToken);
                    request.Headers.UserAgent.ParseAdd("Enactive-Remote");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");

                    using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    using var user = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));

                    // The numeric id, never the login: a login can be renamed and then claimed by somebody else.
                    var id = user.RootElement.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
                    var login = user.RootElement.GetProperty("login").GetString() ?? id;

                    context.Principal = Answer(GitHub, id, login);
                };

                o.Events.OnRemoteFailure = FailedAsync;
            });
        }

        if (providers.Google is { } google)
        {
            builder.AddOpenIdConnect(Google, o =>
            {
                o.SignInScheme = SchemeName;

                o.Authority = providers.GoogleAuthority;
                o.ClientId = google.Id;
                o.ClientSecret = google.Secret;
                o.CallbackPath = $"/auth/{Google}/callback";
                o.ResponseType = "code";
                o.UsePkce = true;
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                o.GetClaimsFromUserInfoEndpoint = false;

                // Who the person is and what they are called; no email, no offline access.
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");

                // Reached only once the handler has checked the id token's signature, issuer, audience,
                // expiry and nonce. The sub is the identity; the email is never read at all.
                o.Events.OnTokenValidated = context =>
                {
                    var token = context.Principal!;
                    var sub = token.FindFirstValue("sub")!;
                    var name = token.FindFirstValue("name") ?? sub;

                    context.Principal = Answer(Google, sub, name);
                    return Task.CompletedTask;
                };

                o.Events.OnRemoteFailure = FailedAsync;
            });
        }

        return builder;
    }

    /// <summary>
    /// The limit on the providers' callbacks, per caller address, for the rate limiter's global slot;
    /// every other request passes it untouched.
    ///
    /// <para>The callbacks are answered inside authentication, before any endpoint, so the limit on the
    /// sign-in endpoints never applies to them. Each one whose state passes makes the gateway post this
    /// service's client secret to the provider's token endpoint, and nothing on the server makes a state
    /// single-use: a client that kept its own state and correlation cookie could replay them with made-up
    /// codes without end, spending this client's standing with GitHub or Google until they blocked
    /// everybody's sign-in. The limiter must therefore run before authentication.</para>
    ///
    /// <para>The caller is the connection's address, which behind the tunnel the forwarded-headers step
    /// has already set from the tunnel's own header.</para>
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> CallbackLimiter()
        => PartitionedRateLimiter.Create<HttpContext, string>(context => IsCallback(context.Request.Path)
            ? RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = CallbacksPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                })
            : RateLimitPartition.GetNoLimiter(""));

    /// <summary><c>/auth/{provider}/callback</c>, for any provider name, configured or not.</summary>
    private static bool IsCallback(PathString path)
        => path.Value is { } value
           && value.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase)
           && value.EndsWith("/callback", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the providers' callback address the public origin, for <c>/auth</c> requests.
    ///
    /// <para>The handlers build the callback from the request's scheme and Host header. Behind the
    /// tunnel every request arrives as plain http from 127.0.0.1, so the callback they built was not
    /// the registered one and the provider refused it. And a Host header is the caller's to write:
    /// a callback built from it would let the caller choose where the provider sends the code.</para>
    /// </summary>
    public static void UsePublicOrigin(this IApplicationBuilder app, ExternalProviders providers)
    {
        if (providers.PublicOrigin is not { } origin)
        {
            return;
        }

        // The authority, which leaves a default port out: the callback has to match the registered one
        // character for character, and https://host:443/... is not https://host/... to a provider.
        var host = HostString.FromUriComponent(origin.Authority);

        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth"))
            {
                context.Request.Scheme = origin.Scheme;
                context.Request.Host = host;
            }

            return next(context);
        });
    }

    /// <summary>
    /// <c>GET /api/providers</c>, <c>GET /auth/{provider}/start</c> and <c>GET /auth/complete</c>.
    /// The callbacks themselves are the handlers', answered before routing.
    /// </summary>
    public static void MapExternalSignIn(
        this IEndpointRouteBuilder app, ExternalProviders providers, string rateLimitPolicy)
    {
        // Anonymous: the panel asks it to know which buttons to draw before anybody is signed in.
        app.MapGet("/api/providers", () => providers.Names);

        // A GET, because it is a link the panel follows. Starting a sign-in cannot sign anybody in to
        // another person's account: that takes a callback with this browser's state, which is the
        // handler's check.
        app.MapGet("/auth/{provider}/start", (string provider) => providers.Offers(provider)
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = CompletePath }, [provider])
                : Results.NotFound())
            .RequireRateLimiting(rateLimitPolicy);

        app.MapGet(CompletePath, CompleteAsync).RequireRateLimiting(rateLimitPolicy);
    }

    /// <summary>
    /// The provider's answer, through the admission list, into a session. Never the provisioning call
    /// that skips the list: that would hand an account to anybody with a GitHub login.
    /// </summary>
    private static async Task<IResult> CompleteAsync(
        HttpContext context, AccountService accounts, CancellationToken ct)
    {
        var external = await context.AuthenticateAsync(SchemeName);

        // Ended in every outcome, before anything else can fail: the answer is good for one sign-in, and
        // a copy left in the browser could be replayed into another for the rest of its ten minutes.
        await context.SignOutAsync(SchemeName);

        var principal = external.Succeeded ? external.Principal : null;
        var provider = principal?.FindFirstValue(ProviderClaim);
        var subject = principal?.FindFirstValue(SubjectClaim);
        var display = principal?.FindFirstValue(DisplayClaim);

        if (provider is null || subject is null || display is null)
        {
            return Results.Redirect(FailedPage);
        }

        switch (await accounts.SignInAsync(provider, subject, display, ct))
        {
            case SignInOutcome.SignedIn signedIn:
                await UserCookie.IssueAsync(context, signedIn.Access, signedIn.SecurityVersion);
                return Results.Redirect(SignedInPage);
            case SignInOutcome.Waiting:
                return Results.Redirect(WaitingPage);
            case SignInOutcome.Refused:
                return Results.Redirect(RefusedPage);
            case SignInOutcome.Disabled:
                // Its own answer, not "refused": a person whose account was disabled is told so, rather
                // than being sent to ask an operator for an account they already have.
                return Results.Redirect(DisabledPage);
            default:
                throw new InvalidOperationException("A sign-in outcome nothing here handles.");
        }
    }

    /// <summary>
    /// What the provider said, as three claims and nothing else. A fresh principal rather than the
    /// handler's: an id token's own claims would ride along in the cookie, and one named like ours
    /// would be read in its place. Internal so a test can make the External cookie's ticket the way the
    /// handlers make it.
    /// </summary>
    internal static ClaimsPrincipal Answer(string provider, string subject, string display)
        => new(new ClaimsIdentity(
            [new Claim(ProviderClaim, provider), new Claim(SubjectClaim, subject), new Claim(DisplayClaim, display)],
            provider));

    /// <summary>
    /// A refused callback - a bad state, a code the provider would not redeem, an id token that failed a
    /// check, the person cancelling at the provider - lands on the panel, which says it did not work.
    /// Without this the handler throws, and the person gets an error page for a routine refusal.
    /// </summary>
    private static Task FailedAsync(RemoteFailureContext context)
    {
        context.Response.Redirect(FailedPage);
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
