namespace Enactive.Server.Tests;

using System.Net;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// A service started for the first time on a machine, with no server.json in its data folder, did not start at all
/// until somebody wrote the file and made a certificate by hand. It now writes both for itself - for this machine only,
/// so being installed still opens nothing to the network (ADR 0002 §5).
/// </summary>
public sealed class AFirstStartWritesSettingsForThisMachineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-server-first", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "server.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    [Fact]
    public void No_settings_in_the_data_folder_get_settings_that_start_and_answer_this_machine_only()
    {
        var said = FirstStart.IfMissing(null, _root, ConfigPath);

        Assert.NotNull(said);
        var options = ServerOptions.Load(ConfigPath, out var unreadable);
        Assert.Null(unreadable);
        Assert.Empty(options!.Problems());
        Assert.All(options.Endpoints(), e => Assert.True(IPAddress.IsLoopback(e.Address)));
        Assert.Equal(["127.0.0.0/8"], options.AllowedSubnets);
        Assert.Contains("localhost", options.HostNames);
        Assert.Empty(options.WorkspaceRoots);
        Assert.True(Enactive.Secrets.Secret.IsProtected(options.Certificate!.Password));
    }

    [Fact]
    public void The_certificate_it_writes_is_one_the_service_presents_for_localhost()
    {
        var said = FirstStart.IfMissing(null, _root, ConfigPath);
        var options = ServerOptions.Load(ConfigPath, out _)!;

        using var certificate = ServerCertificate.Load(options.Certificate!, out var problem);

        Assert.Null(problem);
        Assert.True(certificate!.HasPrivateKey);
        Assert.True(certificate.MatchesHostname("localhost"));
        Assert.True(certificate.NotAfter > DateTime.Now.AddYears(1));
        // The fingerprint said is the one a browser shows for it.
        Assert.Contains(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), said);
    }

    [Fact]
    public void Settings_already_there_are_left_as_they_are()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ConfigPath, "{ \"Listen\": [\"0.0.0.0:9443\"] }");

        Assert.Null(FirstStart.IfMissing(null, _root, ConfigPath));
        Assert.Equal("{ \"Listen\": [\"0.0.0.0:9443\"] }", File.ReadAllText(ConfigPath));
        Assert.False(File.Exists(Path.Combine(_root, FirstStart.CertificateFile)));
    }

    /// <summary>A --config path that is not there is more often mistyped than a first start: nothing is written there.</summary>
    [Fact]
    public void A_settings_file_named_on_the_command_line_is_never_written()
    {
        var named = Path.Combine(_root, "elsewhere.json");

        Assert.Null(FirstStart.IfMissing(named, _root, named));
        Assert.False(File.Exists(named));
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any());
    }

    /// <summary>A certificate already in the folder holds a key somebody may still use; a first start never overwrites it.</summary>
    [Fact]
    public void A_certificate_file_already_in_the_folder_is_kept()
    {
        Directory.CreateDirectory(_root);
        var existing = Path.Combine(_root, FirstStart.CertificateFile);
        File.WriteAllText(existing, "not ours");

        FirstStart.IfMissing(null, _root, ConfigPath);

        Assert.Equal("not ours", File.ReadAllText(existing));
        var options = ServerOptions.Load(ConfigPath, out _)!;
        Assert.NotEqual(existing, options.Certificate!.Path);
        using var certificate = ServerCertificate.Load(options.Certificate, out var problem);
        Assert.Null(problem);
    }
}
