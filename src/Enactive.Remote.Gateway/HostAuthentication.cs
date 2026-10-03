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
/// <para>The identity this establishes IS the HostId and its owner for every later check. Both are
/// read from the computer's own row; nothing downstream takes a host id or an account from the
/// caller, so no Host can act as another, or in another person's account, by asking to.</para>
/// </summary>
public sealed class HostAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    Database database)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Host";

    /// <summary>
    /// The account the computer belongs to. A claim of its own beside the name identifier, which
    /// stays the host id: SignalR keys its connections on that, and replacing it with the owner would
    /// make every computer of one person the same SignalR user.
    /// </summary>
    public const string OwnerClaim = "owner";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = Ids.Hash(header["Bearer ".Length..].Trim());

        await using var connection = await database.OpenAsync(Context.RequestAborted);

        // One indexed lookup on ux_hosts_token, and the owner's row by its primary key. Revoked
        // devices and disabled accounts are refused here AND checked again inside every service
        // call, because a credential withdrawn mid-connection has to stop working at the next thing
        // that Host does, not at its next reconnect.
        var found = await connection.ReadOneAsync(null,
            """
            SELECT h.id, h.owner_id, u.status
            FROM hosts h JOIN users u ON u.id = h.owner_id
            WHERE h.token_hash = @hash AND h.revoked = 0
            """,
            reader => (
                HostId: reader.GetString("id"),
                OwnerId: reader.GetString("owner_id"),
                Status: reader.GetString("status")),
            ("@hash", hash));

        if (found.HostId is null)
        {
            return AuthenticateResult.Fail("Unknown or revoked device credential.");
        }

        // The fault rather than a sentence, so whoever reads the failure gets the code the Host
        // classifies as final, and not a message to parse.
        if (found.Status != "Active")
        {
            return AuthenticateResult.Fail(GatewayFault.AccountDisabled());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, found.HostId), new Claim(OwnerClaim, found.OwnerId)],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
