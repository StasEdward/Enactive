namespace Enactive.Server.Accounts;

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

/// <summary>
/// Signing in to the service and what each role may reach (ADR 0002 §4-§5): a cookie session against the accounts in
/// <see cref="UserStore"/>, three policies each including the one before, and sessions ended when what their account
/// may do changes.
/// </summary>
internal static class ServerAuth
{
    public const string StampClaim = "enactive:stamp";

    public static void Add(IServiceCollection services, UserStore users)
    {
        services.AddSingleton(users);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.LoginPath = "/signin";
                cookie.AccessDeniedPath = "/denied";
                cookie.Cookie.Name = "enactive";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Strict: a link from another site arrives signed out, so nothing there can act in a person's name.
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(10);
                cookie.SlidingExpiration = true;
                // Every request, not only the live page: a disabled account or a lowered role must not keep acting
                // through a request it makes outside the circuit. One row read by key, for a handful of people.
                cookie.Events.OnValidatePrincipal = context =>
                {
                    if (StillValid(users, context.Principal) is null)
                    {
                        context.RejectPrincipal();
                        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(nameof(Role.Viewer), p => p.RequireAssertion(c => RoleOf(c.User) >= Role.Viewer))
            .AddPolicy(nameof(Role.Operator), p => p.RequireAssertion(c => RoleOf(c.User) >= Role.Operator))
            .AddPolicy(nameof(Role.Admin), p => p.RequireAssertion(c => RoleOf(c.User) >= Role.Admin));

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, Revalidating>();
    }

    /// <summary>The claims a signed-in session carries: the name, the role, and the account's stamp at sign-in.</summary>
    public static ClaimsPrincipal PrincipalOf(Account account)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, account.Name),
                new Claim(ClaimTypes.Role, account.Role.ToString()),
                new Claim(StampClaim, account.Stamp)
            ], CookieAuthenticationDefaults.AuthenticationScheme));

    public static Role? RoleOf(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true && Enum.TryParse<Role>(user.FindFirstValue(ClaimTypes.Role), out var role)
            ? role
            : null;

    /// <summary>
    /// The account behind a session, when the session still speaks for it: the account is there, not disabled, and
    /// nothing about what it may do has changed since sign-in (same stamp, same role). Null otherwise.
    /// </summary>
    public static Account? StillValid(UserStore users, ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true || user.FindFirstValue(ClaimTypes.Name) is not { } name)
            return null;
        var account = users.Find(name);
        return account is { Disabled: false } && account.Stamp == user.FindFirstValue(StampClaim)
               && account.Role.ToString() == user.FindFirstValue(ClaimTypes.Role)
            ? account
            : null;
    }

    /// <summary>
    /// Asks again, every minute, whether each open page still speaks for its account. A live page holds a circuit that
    /// makes no new requests, so the cookie check alone would let a disabled account keep its open page for hours.
    /// </summary>
    private sealed class Revalidating(ILoggerFactory logs, UserStore users)
        : RevalidatingServerAuthenticationStateProvider(logs)
    {
        protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

        protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
            => Task.FromResult(StillValid(users, state.User) is not null);
    }
}
