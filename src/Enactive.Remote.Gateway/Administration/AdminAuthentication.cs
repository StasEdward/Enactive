namespace Enactive.Remote.Gateway.Administration;

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

internal static class AdminAuthentication
{
    public const string CookieScheme = "Administrator";
    public const string OidcScheme = "AdministratorOidc";
    public const string Policy = "AdministratorAccess";
    public const string FreshPolicy = "FreshAdministratorAccess";
    public const string CallbackPath = "/admin/auth/callback";
    public const string CookieName = "__Host-Enactive.Admin";

    public static void AddAdministration(this IServiceCollection services, AdminSettings? settings)
    {
        // The store also serves the CLI and retention while the public admin surface is disabled.
        services.AddSingleton<AdminStore>();
        if (settings is null) return;
        services.AddSingleton(settings);
        services.AddSingleton<AdminDirectory>();
        services.AddSingleton<Accounts.AdministrationService>();
        services.AddAuthentication()
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.Domain = null;
                o.Cookie.Path = "/";
                o.ExpireTimeSpan = AdminStore.Lifetime;
                o.SlidingExpiration = false;
                o.Events.OnRedirectToLogin = c => RefuseAsync(c.HttpContext, 401);
                o.Events.OnRedirectToAccessDenied = c => RefuseAsync(c.HttpContext, 403);
                o.Events.OnValidatePrincipal = async c =>
                {
                    var p = c.Principal;
                    if (!settings.Matches(c.Request)
                        || p?.FindFirst("admin_id")?.Value is not { } id
                        || p.FindFirst("admin_session")?.Value is not { } sessionId
                        || !int.TryParse(p.FindFirst("admin_version")?.Value, out var version)
                        || await c.HttpContext.RequestServices.GetRequiredService<AdminStore>()
                            .FindAsync(id, sessionId, version, c.HttpContext.RequestAborted) is not { } session)
                    {
                        c.RejectPrincipal();
                        await c.HttpContext.SignOutAsync(CookieScheme);
                        return;
                    }
                    c.HttpContext.Items[typeof(AdminSession)] = session;
                };
            })
            .AddOpenIdConnect(OidcScheme, o =>
            {
                o.Authority = settings.Authority;
                o.ClientId = settings.ClientId;
                o.ClientSecret = settings.ClientSecret;
                o.SignInScheme = CookieScheme;
                o.CallbackPath = CallbackPath;
                // Do not accidentally acquire the default remote sign-out paths on the user host.
                o.RemoteSignOutPath = "/admin/auth/provider-signout";
                o.SignedOutCallbackPath = "/admin/auth/signed-out";
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.ResponseMode = OpenIdConnectResponseMode.FormPost;
                o.UsePkce = true;
                o.RequireHttpsMetadata = true;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.MapInboundClaims = false;
                // Default claim actions remove acr and issuer before TicketReceived. Keep the
                // signed evidence through protocol validation; the final cookie replaces ALL claims.
                o.ClaimActions.Clear();
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(60);
                o.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Events.OnRedirectToIdentityProvider = c =>
                {
                    c.ProtocolMessage.RedirectUri = new Uri(settings.Origin, CallbackPath).AbsoluteUri;
                    c.ProtocolMessage.Prompt = "login";
                    c.ProtocolMessage.MaxAge = "0";
                    c.ProtocolMessage.AcrValues = settings.MfaAcr;
                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = c =>
                {
                    // Requesting MFA is not evidence that the provider performed it. Check only
                    // signed ID-token claims, before creating any local authority or session.
                    if (!Evidence(c.Principal!, settings.MfaAcr,
                        c.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), out _, out _))
                        c.Fail("Administrative authentication was refused.");
                    return Task.CompletedTask;
                };
                o.Events.OnTicketReceived = async c =>
                {
                    // This event runs AFTER the handler's nonce/protocol checks. Creating a DB
                    // session in OnTokenValidated would leave sessions from callbacks rejected later.
                    var clock = c.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                    if (!Evidence(c.Principal!, settings.MfaAcr, clock.GetUtcNow(), out var identity, out var at)
                        || await c.HttpContext.RequestServices.GetRequiredService<AdminStore>()
                            .OpenAsync(identity!, at, c.HttpContext.RequestAborted) is not { } session)
                    {
                        c.HandleResponse();
                        c.Response.Redirect("/admin/?error=signin");
                        return;
                    }
                    // No intermediate identity cookie and no OAuth tokens that could mint another
                    // session after revocation. The protected cookie refers only to this DB session.
                    c.Principal = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("admin_id", session.AdministratorId),
                        new Claim("admin_session", session.SessionId),
                        new Claim("admin_version", session.Version.ToString(CultureInfo.InvariantCulture))
                    ], CookieScheme));
                    c.Properties = new AuthenticationProperties
                    {
                        IssuedUtc = clock.GetUtcNow(), ExpiresUtc = session.ExpiresAt,
                        IsPersistent = false, AllowRefresh = false
                    };
                    c.ReturnUri = "/admin/";
                };
                o.Events.OnRemoteFailure = c =>
                {
                    // Never put provider errors, claims, authorization codes or arbitrary return
                    // URLs into a redirect or page. All failures have the same fixed destination.
                    c.HandleResponse();
                    c.Response.Redirect("/admin/?error=signin");
                    return Task.CompletedTask;
                };
                o.Events.OnRemoteSignOut = c =>
                {
                    // Provider logout notifications are not our session-revocation protocol.
                    // Administrative logout goes through the CSRF-protected local endpoint.
                    c.HandleResponse();
                    c.Response.StatusCode = 404;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization(o =>
        {
            o.AddPolicy(Policy, p => p.AddAuthenticationSchemes(CookieScheme).RequireAuthenticatedUser());
            o.AddPolicy(FreshPolicy, p => p.AddAuthenticationSchemes(CookieScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => c.Resource is HttpContext h
                    && h.Items[typeof(AdminSession)] is AdminSession session
                    && h.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow() - session.AuthenticatedAt <= AdminStore.FreshWindow));
        });
    }

    internal static bool Evidence(ClaimsPrincipal principal, string acr, DateTimeOffset now,
        out AdminIdentity? identity, out DateTimeOffset at)
    {
        identity = null;
        at = default;
        string? One(string name)
        {
            var values = principal.FindAll(name).ToArray();
            return values.Length == 1 ? values[0].Value : null;
        }
        if (One("acr") != acr || !AdminIdentity.TryCreate(One("iss"), One("sub"), out identity)
            || !long.TryParse(One("auth_time"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        return at <= now.AddSeconds(60) && now - at <= AdminStore.FreshWindow;
    }

    private static Task RefuseAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { code = status == 401 ? "admin_signin_required" : "admin_access_refused" });
    }
}
