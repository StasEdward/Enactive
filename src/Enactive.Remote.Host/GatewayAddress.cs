namespace Enactive.Remote.Host;

/// <summary>
/// Where the hub is, given where the gateway is.
///
/// <para>Here rather than in the application because the path is the Host's own knowledge: this
/// assembly is what speaks the protocol, and the caller types a gateway address, not a hub address.
/// </para>
/// </summary>
public static class GatewayAddress
{
    /// <summary>The hub path, as the gateway maps it.</summary>
    public const string HubPath = "hubs/host";

    /// <summary>
    /// The hub for a gateway at <paramref name="baseUrl"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The address is not an absolute http or https URL. Refused rather than patched up: a typed
    /// address that is nearly a URL is a person's mistake to see, and guessing a scheme for them is
    /// how a token ends up sent over http to somewhere they did not mean.
    /// </exception>
    public static Uri Hub(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"'{baseUrl}' is not a gateway address. It has to be a full http:// or https:// URL.",
                nameof(baseUrl));
        }

        // The trailing slash is the point. Uri's relative resolution replaces the LAST segment of a
        // path that does not end in one, so a gateway hosted under a path - https://example.com/gw -
        // would resolve to https://example.com/hubs/host: a URL that exists, belongs to somebody
        // else, and fails as an authentication problem rather than as a wrong address.
        var root = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");

        return new Uri(root, HubPath);
    }
}
