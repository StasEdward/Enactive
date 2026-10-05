namespace Enactive.Tools.Web;

using System.Net;
using System.Net.Sockets;
using Enactive.Core.Web;

/// <summary>The HTTP clients the web tools use - one per process each, as a client is meant to be.</summary>
public static class WebHttp
{
    private static readonly Lazy<HttpClient> FetchingClient = new(MakeFetching);
    private static readonly Lazy<HttpClient> SearchingClient = new(MakeSearching);

    /// <summary>For fetch_url: connects to public addresses only, and follows no redirect itself.</summary>
    public static HttpClient Fetching => FetchingClient.Value;

    /// <summary>For web_search: to the search server a person configured, wherever it is.</summary>
    public static HttpClient Searching => SearchingClient.Value;

    private static HttpClient MakeFetching()
    {
        var handler = new SocketsHttpHandler
        {
            // fetch_url follows redirects itself, one at a time, so each is checked: followed here, a public page
            // could send the request on to this machine and the tool would never see where it went.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // No proxy: the address checked below must be the address connected to. Through a proxy the check would
            // be of the proxy, and a name that resolves to this network would be fetched from it all the same.
            UseProxy = false,
            // The check where it cannot be got round: at the connection, against the addresses the name resolved
            // to then. Checked only before the request, a name could resolve to a public address for the check and
            // to this machine for the connection.
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                var allowed = addresses.Where(PublicAddress.Allows).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException($"'{host}' is not a public address ({string.Join(", ", addresses.Select(a => a.ToString()))}): "
                        + "fetch_url reads the public web, not this machine or its network.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return WithAgent(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });
    }

    private static HttpClient MakeSearching()
        => WithAgent(new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(30) });

    private static HttpClient WithAgent(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Enactive/1.0 (+https://enactive.dev)");
        return client;
    }
}
