namespace Enactive.Remote.Gateway.Accounts;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;

/// <summary>
/// Signing in by name, with no provider: for a developer's own machine and for tests, which need an
/// account without a GitHub or Google round trip.
///
/// <para>Whoever can reach it can be anyone, because it asks nothing but a name. So it is off unless
/// switched on, and switched on outside Development the gateway does not start at all
/// (<see cref="Enabled"/>): a setting left in a production environment file would otherwise hand every
/// account to anybody who typed its name, and nothing about the running gateway would look wrong.</para>
/// </summary>
public static partial class DevelopmentSignIn
{
    public const string Setting = "ENACTIVE_DEV_SIGNIN";

    /// <summary>The provider its accounts are provisioned under, so they never meet a real identity.</summary>
    public const string Provider = "dev";

    /// <summary>
    /// Whether to map the endpoint. Throws when it is switched on outside Development, and when the
    /// setting is not a boolean: a typo in a security switch is not quietly read as "off" or "on".
    /// </summary>
    public static bool Enabled(IConfiguration configuration, IHostEnvironment environment)
    {
        var value = configuration[Setting];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!bool.TryParse(value, out var enabled))
        {
            throw new InvalidOperationException($"{Setting} is '{value}'; it must be true or false.");
        }

        if (enabled && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{Setting} is true in the {environment.EnvironmentName} environment. The development "
                + "sign-in signs anyone in to any account by name, and runs only in Development.");
        }

        return enabled;
    }

    /// <summary>
    /// <c>POST /api/dev/sign-in {name}</c>: the account of <c>("dev", name)</c>, made on first sight,
    /// and a session of it. Outside the authenticated <c>/api</c> group, since nobody is signed in yet,
    /// so it validates its own antiforgery token - without one, another site could sign a visitor in to
    /// an account of its choosing.
    /// </summary>
    public static void MapDevelopmentSignIn(this IEndpointRouteBuilder app, string rateLimitPolicy)
        => app.MapPost("/api/dev/sign-in", async (
                DevelopmentSignInRequest request, HttpContext context, IAntiforgery antiforgery,
                AccountService accounts, SessionStore sessions, CancellationToken ct) =>
            {
                await antiforgery.ValidateRequestAsync(context);

                // The name is the identity's subject, which is an ASCII column: a name outside it would
                // be stored with question marks, and two different names would become one account.
                if (request.Name is not { } name || !Names().IsMatch(name))
                {
                    throw GatewayFault.BadRequest(
                        "'name' must be 1 to 64 letters, digits, dots, dashes or underscores.");
                }

                var userId = await accounts.ProvisionWithoutAdmissionAsync(Provider, name, name, ct);
                await UserCookie.SignInAsync(context, sessions, userId, ct);

                return Results.Ok(new { id = userId });
            })
            .RequireRateLimiting(rateLimitPolicy);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Names();
}

internal sealed record DevelopmentSignInRequest(string? Name);
