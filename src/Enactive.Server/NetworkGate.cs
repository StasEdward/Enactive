namespace Enactive.Server;

using System.Net;

/// <summary>
/// Who the service answers at all, decided before anything else in the pipeline - before routing, so the browser
/// hub (<c>/_blazor</c>) and every page are behind it, and before sign-in, so a network that should not reach the
/// service learns nothing from it, not even a sign-in page (ADR 0002 §5).
///
/// <para><b>The subnet.</b> The firewall rule the installer writes limits the same thing; this is the second wall,
/// for the day the rule is widened or the service is moved to a machine without it.</para>
///
/// <para><b>The origin.</b> A page on another site can open a WebSocket to the service in a signed-in person's
/// browser, and the browser sends their cookie with it - the hub's own checks do not stop that. A request that says
/// where it comes from (Origin) must come from one of the names the service is reached by. A request without one is
/// a navigation or a non-browser client, which the cookie rules (SameSite=Strict) already keep to this site.</para>
///
/// <para><b>The host.</b> The Host header must be one of those names too: a name somebody else points at the service's
/// address (DNS rebinding) would otherwise make a page of theirs same-origin with it. Checked here, after the subnet,
/// and not by the framework's host filter: that one runs ahead of the whole pipeline, and answered a network the
/// service does not answer with its "Invalid Hostname" page.</para>
///
/// <para>The subnet and origin refusals are a bare 403: the reason is for the log, not for the network being refused.
/// The host's is a 400 with one line - the client is on a network the service answers, and a person who opened it by
/// another name (localhost, where HostNames names the machine) is told it is the name, not the service.</para>
/// </summary>
internal sealed class NetworkGate(IReadOnlyList<IPNetwork> subnets, IReadOnlyList<string> hostNames, ILogger<NetworkGate> log)
{
    public bool Admits(IPAddress? remote)
    {
        if (remote is null)
            return false;
        // An IPv4 client on a dual-stack listener arrives as ::ffff:a.b.c.d, which no IPv4 subnet contains as written.
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        return subnets.Any(s => s.Contains(remote));
    }

    /// <summary>No Origin, or one whose host is a name the service is reached by.</summary>
    public bool TrustsOrigin(string? origin)
        => string.IsNullOrEmpty(origin)
           || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && hostNames.Contains(uri.Host, StringComparer.OrdinalIgnoreCase));

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (!Admits(remote))
        {
            log.LogWarning("Refused {Remote}: not in the subnets the service answers.", remote);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!TrustsOrigin(origin))
        {
            log.LogWarning("Refused {Remote} {Path}: Origin {Origin} is not a name the service is reached by.",
                remote, context.Request.Path, origin);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var host = context.Request.Host;
        if (!AcceptsHost(host))
        {
            // With the names it does answer: a service opened by a name server.json does not list looked broken, and
            // the browser said only "Invalid Hostname".
            log.LogWarning("Refused {Remote} {Path}: Host '{Host}' is not a name the service is reached by (HostNames in server.json: {HostNames}).",
                remote, context.Request.Path, host.Value, string.Join(", ", hostNames));
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Not a name this service is reached by. HostNames in its server.json says which are.");
            return;
        }

        await next(context);
    }

    /// <summary>A Host header that names one of the names the service is reached by; the port is not the name.</summary>
    public bool AcceptsHost(HostString host)
        => host.HasValue && hostNames.Contains(host.Host, StringComparer.OrdinalIgnoreCase);
}
