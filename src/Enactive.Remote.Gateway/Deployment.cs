namespace Enactive.Remote.Gateway;

using System.Net;

/// <summary>
/// What changes when the gateway is reached through a tunnel rather than directly.
///
/// <para>`remote.enactive.dev` is served through a Cloudflare tunnel: `cloudflared` runs on the
/// same machine and makes an OUTWARD connection to Cloudflare, so nothing on the server listens on
/// a public port at all. Every request the gateway sees therefore arrives from 127.0.0.1, and two
/// things it relies on quietly stop working.</para>
///
/// <para><b>The request looks like plain HTTP</b>, because the last hop is. `UseHttpsRedirection`
/// would answer a perfectly good HTTPS request with a redirect to a port it cannot name.</para>
///
/// <para><b>Every visitor has the same IP.</b> The login rate limiter partitions by remote address,
/// so ten failed attempts from anywhere in the world would share one bucket - and one stranger
/// guessing at the key would lock the owner out of their own panel. A limiter that cannot tell two
/// people apart is not a limiter; it is a denial of service with a schedule.</para>
/// </summary>
public static class Deployment
{
    /// <summary>Set to true on a machine where cloudflared is in front of this process.</summary>
    public const string BehindTunnelSetting = "ENACTIVE_BEHIND_TUNNEL";

    /// <summary>
    /// The header Cloudflare puts the real client's address in.
    ///
    /// <para>Not <c>X-Forwarded-For</c>: a client can send one of those, and what arrives is then a
    /// list somebody else partly wrote. Cloudflare sets <c>CF-Connecting-IP</c> itself and strips
    /// any copy the client supplied, so it is a single value from one author.</para>
    /// </summary>
    public const string ClientAddressHeader = "CF-Connecting-IP";

    public static bool BehindTunnel(IConfiguration configuration)
        => configuration.GetValue<bool>(BehindTunnelSetting);

    /// <summary>
    /// Refuses to start if this process would accept connections from anywhere but this machine
    /// while trusting a header about who sent them.
    ///
    /// <para><b>This is the whole security argument, and it is enforced here rather than described
    /// in a runbook.</b> Trusting <see cref="ClientAddressHeader"/> is safe for exactly one reason:
    /// the only thing that can reach the socket is <c>cloudflared</c>, on this machine, and the
    /// header cannot be forged by anyone who is not already on it. Bind the same process to
    /// <c>0.0.0.0</c> and that reason evaporates - anybody may then connect directly and name
    /// themselves whatever they like, which turns the rate limiter and every log line about who did
    /// what into fiction. It would look configured and enforce nothing.</para>
    ///
    /// <para>So the two settings are not independent, and the process will not run with one of them
    /// on and the other wrong.</para>
    /// </summary>
    public static void RequireLoopbackListeners(IConfiguration configuration)
    {
        // Kestrel's own key first, then the environment variable that usually sets it. Whichever
        // one is present is the one that decides where it listens.
        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];

        if (string.IsNullOrWhiteSpace(urls))
        {
            // Nothing configured means Kestrel's default, which is localhost. Saying "fine" to an
            // absence is usually the mistake; here the absence has one known meaning.
            return;
        }

        var exposed = urls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => !IsLoopback(url))
            .ToArray();

        if (exposed.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BehindTunnelSetting} is on, so this gateway trusts the {ClientAddressHeader} header "
                + "to say who is calling - which is only true while cloudflared is the only thing that "
                + $"can reach it. It is configured to listen on {string.Join(", ", exposed)}, where "
                + "anyone could connect directly and set that header themselves. Bind it to localhost, "
                + $"or turn {BehindTunnelSetting} off and put a proxy in front of it.");
        }
    }

    /// <summary>
    /// True for a URL this machine alone can reach. A host that is not an address at all -
    /// <c>*</c>, <c>+</c>, a domain name - is not loopback: those are the wildcards that mean
    /// "everything", and a name is somebody else's to resolve.
    /// </summary>
    private static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(parsed.Host, out var address) && IPAddress.IsLoopback(address));
    }
}
