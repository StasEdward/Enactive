namespace Enactive.Remote.Gateway.Administration;

/// <summary>Off unless fully configured. MFA evidence comes from this explicitly trusted issuer.</summary>
internal sealed record AdminSettings(Uri Origin, string Authority, string ClientId, string ClientSecret, string MfaAcr)
{
    public const string OriginKey = "ENACTIVE_ADMIN_ORIGIN";
    public const string AuthorityKey = "ENACTIVE_ADMIN_AUTHORITY";
    public const string ClientIdKey = "ENACTIVE_ADMIN_CLIENT_ID";
    public const string ClientSecretKey = "ENACTIVE_ADMIN_CLIENT_SECRET";
    public const string MfaAcrKey = "ENACTIVE_ADMIN_MFA_ACR";

    public static AdminSettings? Read(IConfiguration config, Uri? publicOrigin)
    {
        string[] keys = [OriginKey, AuthorityKey, ClientIdKey, ClientSecretKey, MfaAcrKey];
        if (keys.All(k => string.IsNullOrWhiteSpace(config[k]))) return null;
        foreach (var key in keys)
            if (string.IsNullOrWhiteSpace(config[key])) throw new InvalidOperationException($"Set {key} to enable administration.");
        if (!Uri.TryCreate(config[OriginKey]!.Trim(), UriKind.Absolute, out var origin)
            || origin.Scheme != "https" || origin.PathAndQuery != "/" || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException($"{OriginKey} must be an HTTPS origin without a path.");
        // Cookies are scoped by host, not port. A different port on the public host would still
        // send the administrative cookie to the ordinary panel's server.
        if (publicOrigin is not null && origin.Host.Equals(publicOrigin.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The administrative hostname must differ from the public panel hostname.");
        var authority = config[AuthorityKey]!.Trim();
        if (!AdminIdentity.ValidIssuer(authority))
            throw new InvalidOperationException($"{AuthorityKey} must be an HTTPS OIDC authority.");
        var acr = config[MfaAcrKey]!.Trim();
        // acr_values is a space-delimited protocol field. We require ONE exact signed value,
        // not a list whose weakest member could accidentally become sufficient for administration.
        if (acr.Length > 256 || acr.Any(char.IsWhiteSpace) || acr.Any(char.IsControl))
            throw new InvalidOperationException($"{MfaAcrKey} must name one MFA authentication context.");
        return new(origin, authority, config[ClientIdKey]!.Trim(), config[ClientSecretKey]!, acr);
    }

    public bool Matches(HttpRequest request)
        => Uri.TryCreate($"{request.Scheme}://{request.Host}/", UriKind.Absolute, out var origin) && origin == Origin;
}

/// <summary>Exact issuer and subject from a validated ID token. Never email, display name or a role claim.</summary>
internal sealed record AdminIdentity
{
    public string Issuer { get; }
    public string Subject { get; }
    private AdminIdentity(string issuer, string subject) => (Issuer, Subject) = (issuer, subject);

    public static bool TryCreate(string? issuer, string? subject, out AdminIdentity? identity)
    {
        identity = null;
        if (!ValidIssuer(issuer) || subject is not { Length: > 0 and <= 255 }
            || subject.Any(c => c < '!' || c > '~')) return false;
        identity = new(issuer!, subject);
        return true;
    }

    internal static bool ValidIssuer(string? issuer)
        => issuer is { Length: > 0 and <= 512 } && issuer.All(c => c is >= '!' and <= '~')
            && Uri.TryCreate(issuer, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
