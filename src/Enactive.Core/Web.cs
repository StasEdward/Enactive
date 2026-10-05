namespace Enactive.Core.Web;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// What a person has allowed of the web: whether the tools that read it exist at all, where search goes, and
/// whether they are used without a question each time.
///
/// <para><b>Off unless turned on.</b> Reading the web is reading outside this machine, and asking for a page is
/// sending something out of it: a URL can carry anything the model puts in it, a search query is written from
/// the task. So the tools exist only when a person has turned them on, and a person gives them to a role.</para>
/// </summary>
/// <param name="Enabled">Whether fetch_url exists.</param>
/// <param name="SearchUrl">The search server web_search asks - a SearXNG instance, e.g. http://localhost:8888.
/// Blank: no web_search.</param>
public sealed record WebAccess(bool Enabled, string SearchUrl)
{
    public static readonly WebAccess None = new(false, "");

    /// <summary>
    /// Use the web tools without the approval question. Off unless a person turns it on - and with it off, a run
    /// nobody is watching is not offered them (a question nobody can answer is a refusal).
    /// </summary>
    public bool UseWithoutAsking { get; init; }

    /// <summary>
    /// Whether web_search exists: on, and an http or https address for the server. Anything else would be a tool
    /// that fails every call on an address nobody can search - said in the pane instead.
    /// </summary>
    public bool CanSearch => Enabled
        && Uri.TryCreate(SearchUrl.Trim(), UriKind.Absolute, out var server) && server.Scheme is "http" or "https";
}

/// <summary>
/// Whether an address is on the public internet - the only addresses fetch_url connects to.
///
/// <para>A URL the model writes is not checked by anybody before it is fetched. Allowed to reach this machine or
/// its network, fetch_url would read what is served there - a local admin page, a router, a cloud machine's
/// metadata endpoint - for a model that has the request's text and whatever pages it read in front of it.
/// The commands can reach those too, and are asked about; this tool is meant to read the web.</para>
/// </summary>
public static class PublicAddress
{
    public static bool Allows(IPAddress address)
    {
        // An IPv4 address written as IPv6 (::ffff:192.168.1.1) is the IPv4 address, and is judged as one.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // "this network"
                     || b[0] == 10                               // private
                     || b[0] == 127                              // this machine
                     || (b[0] == 169 && b[1] == 254)             // link-local, and a cloud machine's metadata endpoint
                     || (b[0] == 172 && b[1] is >= 16 and <= 31) // private
                     || (b[0] == 192 && b[1] == 168)             // private
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)// carrier-grade NAT, a provider's own network
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)  // protocol assignments
                     || (b[0] == 198 && b[1] is 18 or 19)        // benchmarking
                     || b[0] >= 224);                            // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(IPAddress.IsLoopback(address)
                     || address.Equals(IPAddress.IPv6None)       // ::
                     || address.IsIPv6LinkLocal
                     || address.IsIPv6SiteLocal
                     || address.IsIPv6Multicast
                     || (b[0] & 0xFE) == 0xFC);                  // unique local, fc00::/7
        }

        return false;
    }
}
