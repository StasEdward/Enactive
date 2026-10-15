namespace Enactive.Server;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Enactive.Secrets;

/// <summary>
/// The certificate the service presents, from the store or a .pfx file - or why there is none it can use. The
/// service does not start without one: it speaks HTTPS only, and a plain listener "until a certificate is set up"
/// is the one that stays (ADR 0002 §5).
/// </summary>
internal static class ServerCertificate
{
    public static X509Certificate2? Load(CertificateOptions options, out string? problem, DateTimeOffset? now = null)
    {
        problem = null;
        X509Certificate2? certificate;
        try
        {
            certificate = string.IsNullOrWhiteSpace(options.Thumbprint)
                ? FromFile(options, out problem)
                : FromStore(options, out problem);
        }
        catch (CryptographicException ex)
        {
            problem = "The certificate could not be opened: " + ex.Message;
            return null;
        }

        if (certificate is null)
            return null;

        // Checked here rather than left to the browsers: a service that starts with an expired certificate is one
        // every user is taught to click through a warning for, which is the habit HTTPS on a LAN must not build.
        var at = (now ?? DateTimeOffset.Now).LocalDateTime;
        if (at > certificate.NotAfter || at < certificate.NotBefore)
        {
            problem = $"The certificate {certificate.Subject} is valid from {certificate.NotBefore.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)} to "
                      + $"{certificate.NotAfter.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}, not now.";
            certificate.Dispose();
            return null;
        }

        if (!certificate.HasPrivateKey)
        {
            problem = $"The certificate {certificate.Subject} has no private key the service can use.";
            certificate.Dispose();
            return null;
        }

        return certificate;
    }

    private static X509Certificate2? FromFile(CertificateOptions options, out string? problem)
    {
        problem = null;
        var path = options.Path!;
        if (!File.Exists(path))
        {
            problem = $"There is no certificate file at {path}.";
            return null;
        }

        var password = Secret.Unprotect(options.Password);
        if (!string.IsNullOrEmpty(options.Password) && password.Length == 0)
        {
            // DPAPI for the current user: written by another account, or on another machine.
            problem = "Certificate.Password could not be read by this account. Protect it again as the account the "
                      + "service runs under (Enactive.Server --protect).";
            return null;
        }

        // UserKeySet, not EphemeralKeySet: Windows' TLS (SChannel) cannot sign a handshake with a key that lives only in
        // the process, and the service then accepts connections and drops every one of them (seen 2026-10-11).
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.UserKeySet);
    }

    private static X509Certificate2? FromStore(CertificateOptions options, out string? problem)
    {
        problem = null;
        if (!Enum.TryParse<StoreLocation>(options.Store, ignoreCase: true, out var location))
        {
            problem = $"Certificate.Store '{options.Store}' is neither LocalMachine nor CurrentUser.";
            return null;
        }

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var thumbprint = options.Thumbprint!.Replace(" ", "", StringComparison.Ordinal);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (found.Count == 0)
        {
            problem = $"No certificate with thumbprint {thumbprint} is in {location}\\My.";
            return null;
        }
        return found[0];
    }
}
