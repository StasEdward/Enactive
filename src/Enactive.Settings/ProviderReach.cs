namespace Enactive.Settings;

using System.Net;

/// <summary>
/// Whether a provider's address is one that costs nothing to call.
///
/// <para><b>What the question really is.</b> The work split asks "how much of this run did we BUY".
/// So the line is not "this exact machine" — a model answering on the box under your desk was not
/// bought either, and putting its tokens in the paid column is the one answer that is certainly
/// wrong. Loopback, this machine by name, and a private network are all on the free side; a public
/// address is not.</para>
///
/// <para><b>Why this is not a string search.</b> It was one:
/// <c>url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("[::1]")</c>. That
/// reads an Ollama at <c>http://192.168.1.50:11434</c> or at <c>http://MYPC:11434</c> as a cloud
/// provider and files free tokens under money spent — and it would read
/// <c>https://localhost.example.com</c>, a perfectly public host, as local. A URL has a host field;
/// asking for it is not harder than searching the whole string, and it cannot be fooled by either.
/// </para>
///
/// <para>Unparseable is not local. A rule that cannot tell must not guess in the direction that
/// makes the numbers look better.</para>
/// </summary>
public static class ProviderReach
{
    public static bool Local(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        var text = baseUrl!.Trim();

        // A bare "localhost:11434" is a host and a port to a person and a relative path to Uri.
        if (!text.Contains("//", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (host.Length == 0)
            return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // This machine by its own name, with or without a .local suffix - what a person types when
        // the model runs here but the endpoint was written down from another box.
        var machine = Environment.MachineName;
        if (machine.Length > 0
            && (string.Equals(host, machine, StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, machine + ".local", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Anything else that is a NAME is a name somebody has to resolve, and resolving it here
        // would mean a DNS lookup on a settings screen. ".local" is the one suffix that says
        // "this network" by definition (mDNS), so it is read and the rest are not.
        if (!IPAddress.TryParse(host, out var address))
            return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

        return IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || PrivateV4(address);
    }

    /// <summary>
    /// RFC 1918 and link-local: 10/8, 172.16/12, 192.168/16, 169.254/16. A model at one of these
    /// is on your own network and nobody billed you for it.
    /// </summary>
    private static bool PrivateV4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            172 => octets[1] >= 16 && octets[1] <= 31,
            192 => octets[1] == 168,
            169 => octets[1] == 254,
            _ => false
        };
    }
}
