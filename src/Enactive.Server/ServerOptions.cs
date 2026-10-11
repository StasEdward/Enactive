namespace Enactive.Server;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Where the service takes its certificate from: the Windows certificate store, or a .pfx file.</summary>
/// <param name="Thumbprint">A certificate in the store, by thumbprint - the way to give one the CA issued.</param>
/// <param name="Store">The store it is in: <c>LocalMachine</c> (the default) or <c>CurrentUser</c> of the service's account.</param>
/// <param name="Path">A .pfx file, for a self-signed certificate whose fingerprint the users check once.</param>
/// <param name="Password">The .pfx file's password, DPAPI-protected for the service's account ("dpapi:..."), never in the clear.</param>
internal sealed record CertificateOptions(
    string? Thumbprint = null,
    string Store = "LocalMachine",
    string? Path = null,
    string? Password = null);

/// <summary>
/// The service's own settings: where it listens, with which certificate, for whom. Kept in <c>server.json</c>
/// beside the service account's <c>settings.json</c>, apart from it: the engine's settings are the same file the
/// desktop has, and these are not settings any other host reads.
///
/// <para><b>Nothing here has a default that opens it up.</b> No addresses means it listens nowhere, no subnets means
/// it answers nobody, no certificate means it does not start: a fresh service on a network must not be reachable
/// by being installed and forgotten (ADR 0002 §5).</para>
/// </summary>
internal sealed record ServerOptions
{
    /// <summary>Where it listens: <c>"10.0.0.5:8443"</c>, <c>"0.0.0.0:8443"</c>, <c>"[::]:8443"</c>. HTTPS on each; there is no plain HTTP.</summary>
    public IReadOnlyList<string> Listen { get; init; } = [];

    /// <summary>The networks it answers, in CIDR (<c>"10.20.0.0/16"</c>). Anything else gets nothing at all.</summary>
    public IReadOnlyList<string> AllowedSubnets { get; init; } = [];

    /// <summary>The names the service is reached by (<c>"enactive.lab.local"</c>); the browser hub accepts only these as origins.</summary>
    public IReadOnlyList<string> HostNames { get; init; } = [];

    public CertificateOptions? Certificate { get; init; }

    /// <summary>How many runs go at once; each run's own parallel steps are its own (ADR 0002 §9).</summary>
    public int MaxActiveRuns { get; init; } = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>Reads the file. Null and why, when it is missing or not readable as settings.</summary>
    public static ServerOptions? Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            problem = $"There is no {System.IO.Path.GetFileName(path)} at {path}. The service listens nowhere until it says where, "
                      + "for whom and with which certificate - run deploy\\install-server.ps1, or write it by hand.";
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), Json)
                   ?? throw new JsonException("The file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"{path} could not be read: {ex.Message}";
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Everything that keeps the service from starting safely, in words; empty when nothing does.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Listen.Count == 0)
            problems.Add("Listen names no address, so the service would listen nowhere.");
        foreach (var listen in Listen)
            if (!IPEndPoint.TryParse(listen, out var endpoint) || endpoint.Port == 0)
                problems.Add($"Listen: '{listen}' is not an address and port (\"10.0.0.5:8443\").");

        if (AllowedSubnets.Count == 0)
            problems.Add("AllowedSubnets names no network, so the service would answer nobody.");
        foreach (var subnet in AllowedSubnets)
            if (!IPNetwork.TryParse(subnet, out _))
                problems.Add($"AllowedSubnets: '{subnet}' is not a network in CIDR (\"10.20.0.0/16\").");

        if (HostNames.Count == 0)
            problems.Add("HostNames names nothing, so no browser page could connect to the service.");

        if (Certificate is null || (string.IsNullOrWhiteSpace(Certificate.Thumbprint) && string.IsNullOrWhiteSpace(Certificate.Path)))
            problems.Add("Certificate names neither a thumbprint nor a .pfx file. The service speaks HTTPS only, and does not start without one.");
        else if (!string.IsNullOrWhiteSpace(Certificate.Thumbprint) && !string.IsNullOrWhiteSpace(Certificate.Path))
            problems.Add("Certificate names both a thumbprint and a .pfx file; name one, so it is clear which one the service presents.");
        else if (!string.IsNullOrEmpty(Certificate.Password) && !Secrets.Secret.IsProtected(Certificate.Password))
            problems.Add("Certificate.Password is written in the clear. Protect it for the service's account (Enactive.Server --protect).");

        if (MaxActiveRuns < 1)
            problems.Add("MaxActiveRuns must be at least 1.");

        return problems;
    }

    /// <summary>The listen addresses, parsed. Call after <see cref="Problems"/> found none.</summary>
    public IEnumerable<IPEndPoint> Endpoints() => Listen.Select(IPEndPoint.Parse);

    /// <summary>The subnets, parsed. Call after <see cref="Problems"/> found none.</summary>
    public IReadOnlyList<IPNetwork> Subnets() => AllowedSubnets.Select(IPNetwork.Parse).ToList();
}
