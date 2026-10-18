namespace Enactive.Server;

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Enactive.Secrets;

/// <summary>
/// What the service writes for itself the first time it starts with no <c>server.json</c> in its data folder: settings
/// that open it to this machine only, and a self-signed certificate for <c>localhost</c> to present.
///
/// <para>Without it a fresh machine's service did not start at all ("There is no server.json") until somebody wrote the
/// file and made a certificate by hand. It still opens nothing to the network: it listens on <c>127.0.0.1</c> and
/// answers <c>127.0.0.0/8</c>, so a service installed and forgotten is reachable from its own machine and from no other
/// (ADR 0002 §5) - opening it to a network stays a person's decision, in the file or through
/// <c>deploy\install-server.ps1</c>.</para>
///
/// <para>Only for the data folder's own <c>server.json</c>: a file named with <c>--config</c> that is not there is a
/// mistyped path more often than a first start, and writing settings at a path somebody mistyped hides the mistake.</para>
/// </summary>
internal static class FirstStart
{
    /// <summary>The port it listens on: the one the install script and the docs use.</summary>
    public const int Port = 8443;

    public const string CertificateFile = "server-certificate.pfx";

    /// <summary>
    /// Two years. A certificate the service generated is still one the users check the fingerprint of once; a short life
    /// would have them do it again every few months, and an expired one stops the service (ServerCertificate) - so the
    /// date is said when it is written.
    /// </summary>
    private static readonly TimeSpan Life = TimeSpan.FromDays(730);

    /// <summary>
    /// Writes <paramref name="configPath"/> and a certificate beside it, when the data folder has no settings and none
    /// were named. Null when there was nothing to do; otherwise what was written, in words, for the console and the
    /// service's start log.
    /// </summary>
    public static string? IfMissing(string? namedConfig, string dataFolder, string configPath, DateTimeOffset? now = null)
    {
        if (namedConfig is not null || File.Exists(configPath))
            return null;

        Directory.CreateDirectory(dataFolder);
        var at = now ?? DateTimeOffset.Now;
        var certificatePath = FreePath(Path.Combine(dataFolder, CertificateFile));
        var (fingerprint, until) = WriteCertificate(certificatePath, at, out var password);

        var options = new ServerOptions
        {
            Listen = [$"{IPAddress.Loopback}:{Port}"],
            AllowedSubnets = ["127.0.0.0/8"],
            // Both names a browser on this machine uses; NetworkGate refuses a Host the list does not have.
            HostNames = ["localhost", IPAddress.Loopback.ToString()],
            Certificate = new CertificateOptions(Path: certificatePath, Password: Secret.Protect(password))
        };
        // Through a temporary file: a service stopped halfway must not leave a server.json that names a certificate
        // and is cut off before the rest - the next start would read it, refuse it, and never write it again.
        var temporary = configPath + ".new";
        File.WriteAllText(temporary, options.ToJson());
        File.Move(temporary, configPath, overwrite: false);

        return $"There was no server.json, so one was written at {configPath}: the service listens on https://localhost:{Port}/ "
               + "and answers this machine only. Its certificate is a new self-signed one for localhost, "
               + $"{certificatePath}, valid until {until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}; its SHA-256 "
               + $"fingerprint is {fingerprint} - the one a browser shows when it asks whether to trust it. To reach the "
               + "service from other machines, set Listen, AllowedSubnets and HostNames in that file (and a certificate "
               + "for that name), or run deploy\\install-server.ps1. Tasks are started from the browser only under "
               + "WorkspaceRoots, which names none yet; an account to sign in with: Enactive.Server --add-user <name> --role Admin.";
    }

    /// <summary>A path that is not there yet: a certificate already in the folder keeps its key, never overwritten.</summary>
    private static string FreePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        for (var n = 2; ; n++)
            if (!File.Exists($"{stem}-{n}.pfx"))
                return $"{stem}-{n}.pfx";
    }

    private static (string Fingerprint, DateTimeOffset Until) WriteCertificate(string path, DateTimeOffset now, out string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost, O=Enactive Server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], critical: false));

        // A day back: a clock a little behind on this machine, or a browser's, would otherwise see it as not valid yet.
        var until = now.Add(Life);
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), until);

        // A password, kept DPAPI-protected for this account in server.json: the .pfx holds the private key, and a file
        // copied off the machine without a password is the key itself.
        password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return (certificate.GetCertHashString(HashAlgorithmName.SHA256), until);
    }
}
