namespace Enactive.Remote.Gateway.Administration;

using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

internal static class AdminEndpoints
{
    public static void UseAdministrativeOrigin(this WebApplication app, AdminSettings? settings)
    {
        // Run before static files AND authentication callbacks. A route-prefix check after
        // authentication would let the OIDC handler process a callback on the public user host.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var adminPath = path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase);
            var adminHost = settings?.Matches(context.Request) == true;
            if ((adminPath && !adminHost) || (adminHost && !adminPath && path != "/health"))
            {
                context.Response.StatusCode = 404;
                return;
            }
            if (adminPath) context.Response.Headers.CacheControl = "no-store";
            await next();
        });
    }

    public static void MapAdministration(this WebApplication app, AdminSettings? settings)
    {
        if (settings is null) return;
        app.MapGet("/admin/auth/start", () => Results.Challenge(
            new AuthenticationProperties { RedirectUri = "/admin/" }, [AdminAuthentication.OidcScheme]))
            .RequireRateLimiting(RequestLimits.Auth);

        app.MapGet("/admin/api/session", async (HttpContext context, IAntiforgery csrf) =>
        {
            var result = await context.AuthenticateAsync(AdminAuthentication.CookieScheme);
            // Never issue an admin CSRF token against an ordinary user principal, even when a
            // caller manually supplies their user cookie on this origin.
            context.User = result.Succeeded ? result.Principal! : new ClaimsPrincipal(new ClaimsIdentity());
            var session = context.Items[typeof(AdminSession)] as AdminSession;
            return Results.Ok(new
            {
                authenticated = session is not null,
                administratorId = session?.AdministratorId,
                expiresAt = session?.ExpiresAt,
                csrfToken = csrf.GetAndStoreTokens(context).RequestToken
            });
        }).RequireRateLimiting(RequestLimits.Auth);

        var api = app.MapGroup("/admin/api").RequireAuthorization(AdminAuthentication.Policy)
            .RequireRateLimiting(RequestLimits.Auth);
        api.AddEndpointFilter(async (invocation, next) =>
        {
            if (!HttpMethods.IsGet(invocation.HttpContext.Request.Method))
                await invocation.HttpContext.RequestServices.GetRequiredService<IAntiforgery>()
                    .ValidateRequestAsync(invocation.HttpContext);
            return await next(invocation);
        });
        api.MapPost("/signout", async (HttpContext context, AdminStore store, CancellationToken ct) =>
        {
            await store.CloseAsync((AdminSession)context.Items[typeof(AdminSession)]!, ct);
            await context.SignOutAsync(AdminAuthentication.CookieScheme);
            return Results.NoContent();
        });
        // A fresh-authentication check for the UI before sensitive forms are shown. Future mutations
        // must require the SAME policy themselves; checking this endpoint is not an authorization grant.
        api.MapGet("/fresh", () => Results.NoContent()).RequireAuthorization(AdminAuthentication.FreshPolicy);
    }
}
