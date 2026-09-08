namespace Enactive.Remote.Gateway;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

/// <summary>
/// Who a Host is: the bearer token on the request, hashed and looked up.
///
/// <para><b>Not the query string.</b> SignalR will happily put an access token there, and a URL is
/// the one place a credential is guaranteed to be written down - proxy logs, browser history, a
/// referer header. The header is the only place this reads from, and that is a decision rather than
/// an omission.</para>
///
/// <para>The identity this establishes IS the HostId for every later check. Nothing downstream
/// takes a host id from the caller, so no Host can act as another by asking to.</para>
/// </summary>
public sealed class HostAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    Database database)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Host";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = Ids.Hash(header["Bearer ".Length..].Trim());

        await using var connection = await database.OpenAsync(Context.RequestAborted);

        // One indexed lookup on ux_hosts_token. Revoked devices are excluded here AND checked again
        // inside every service call, because a credential withdrawn mid-connection has to stop
        // working at the next thing that Host does, not at its next reconnect.
        var hostId = await connection.ReadOneAsync(null,
            "SELECT id FROM hosts WHERE token_hash = @hash AND revoked = 0",
            reader => reader.GetString("id"), ("@hash", hash));

        if (hostId is null)
        {
            return AuthenticateResult.Fail("Unknown or revoked device credential.");
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, hostId)], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
