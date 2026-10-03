namespace Enactive.Remote.Gateway.Administration;

using System.Security.Claims;
using Enactive.Remote.Gateway.Accounts;
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

    private static AdminMutation Mutation(HttpContext context, int? version)
    {
        if (version is null or < 0) throw GatewayFault.BadRequest("A record version is required. Refresh the page.");
        return new((AdminSession)context.Items[typeof(AdminSession)]!, version.Value);
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
        api.MapGet("/overview", (AdminDirectory directory, CancellationToken ct) => directory.OverviewAsync(ct)).RequireRateLimiting(RequestLimits.AdminRead);
        api.MapGet("/users", (AdminDirectory directory, string? search, string? state, string? after, int? size, CancellationToken ct)
            => directory.UsersAsync(search, state, after, size ?? 25, ct)).RequireRateLimiting(RequestLimits.AdminRead);
        api.MapGet("/registrations", (AdminDirectory directory, string? search, string? state, string? after, int? size, CancellationToken ct)
            => directory.RegistrationsAsync(search, state, after, size ?? 25, ct)).RequireRateLimiting(RequestLimits.AdminRead);
        api.MapGet("/users/{id}", (AdminDirectory directory, string id, CancellationToken ct) => directory.UserAsync(id, ct)).RequireRateLimiting(RequestLimits.AdminRead);
        api.MapPost("/users/{id}/access", async (HttpContext context, AdministrationService service,
            string id, AdminAccessRequest request, CancellationToken ct) =>
        {
            AdminDirectory.UserId(id);
            var mutation = Mutation(context, request.ExpectedVersion);
            switch (request.Action)
            {
                case "disable":
                    var result = await service.DisableAccountAsync(id, ct, mutation)
                        ?? throw GatewayFault.NotFound("Account not found.");
                    return Results.Ok(new { action = "disable", withdrawnCommands = result.WithdrawnCommands });
                case "enable":
                    if (!await service.EnableAccountAsync(id, ct, mutation)) throw GatewayFault.NotFound("Account not found.");
                    break;
                case "revoke-sessions":
                    if (!await service.RevokeSessionsAsync(id, ct, mutation)) throw GatewayFault.NotFound("Account not found.");
                    break;
                default: throw GatewayFault.BadRequest("Unknown access action.");
            }
            return Results.Ok(new { action = request.Action });
        }).RequireAuthorization(AdminAuthentication.FreshPolicy);
        api.MapPost("/registrations/decision", async (HttpContext context, AdministrationService service,
            AdminDecisionRequest request, CancellationToken ct) =>
        {
            if (request.Provider is null || request.Subject is null ||
                !AdmissionIdentity.TryParse(request.Provider + ":" + request.Subject, out var identity) ||
                identity.Provider != request.Provider || identity.Subject != request.Subject)
                throw GatewayFault.BadRequest("Invalid provider identity.");
            var state = request.Decision switch
            {
                "approve" => AdmissionState.Approved,
                "refuse" => AdmissionState.Refused,
                _ => throw GatewayFault.BadRequest("Unknown registration decision.")
            };
            return Results.Ok(await service.DecideAdmissionAsync(identity, state, ct, Mutation(context, request.ExpectedVersion)));
        }).RequireAuthorization(AdminAuthentication.FreshPolicy);
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

internal sealed record AdminAccessRequest(string? Action, int? ExpectedVersion);
internal sealed record AdminDecisionRequest(string? Provider, string? Subject, string? Decision, int? ExpectedVersion);
