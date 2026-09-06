namespace Enactive.Server;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

public sealed class HostAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, GatewayStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
        var hash = GatewayService.Hash(header[7..]);
        var host = store.Read(s => s.Hosts.Find(h => !h.Revoked && h.TokenHash == hash));
        if (host is null) return Task.FromResult(AuthenticateResult.Fail("Invalid device credential."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, host.Id)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
