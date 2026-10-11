namespace Enactive.Server.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Enactive.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the service needs before it starts at all (server plan 1.1): settings that say where, for whom and with
/// which certificate - none of it defaulting open - a certificate it can present, and a data folder no other service
/// is using. And, once up, a pipeline that answers.
/// </summary>
public sealed class ServerStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-server", Guid.NewGuid().ToString("N"));

    public ServerStartTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static ServerOptions Good(CertificateOptions? certificate = null) => new()
    {
        Listen = ["0.0.0.0:8443"],
        AllowedSubnets = ["10.20.0.0/16"],
        HostNames = ["enactive.lab.local"],
        Certificate = certificate ?? new CertificateOptions(Path: "server.pfx")
    };

    // ── settings ────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_that_say_where_for_whom_and_with_what_have_no_problems()
        => Assert.Empty(Good().Problems());

    /// <summary>A service installed and forgotten must not be reachable for having been installed: nothing defaults open.</summary>
    [Fact]
    public void Empty_settings_open_nothing_and_say_each_thing_missing()
    {
        var problems = new ServerOptions().Problems();

        Assert.Contains(problems, p => p.Contains("listen nowhere"));
        Assert.Contains(problems, p => p.Contains("answer nobody"));
        Assert.Contains(problems, p => p.Contains("HostNames"));
        Assert.Contains(problems, p => p.Contains("does not start without one"));
    }

    [Theory]
    [InlineData("localhost:8443")]
    [InlineData("10.0.0.5")]
    [InlineData("10.0.0.5:0")]
    public void A_listen_entry_that_is_not_an_address_and_port_is_refused(string listen)
        => Assert.Contains(Good() with { Listen = [listen] } is var o ? o.Problems() : [], p => p.Contains($"'{listen}'"));

    [Fact]
    public void A_subnet_that_is_not_cidr_is_refused()
        => Assert.Contains((Good() with { AllowedSubnets = ["10.20.0.0/40"] }).Problems(), p => p.Contains("CIDR"));

    /// <summary>The .pfx password is a secret: in server.json it is protected for the service's account, never in the clear.</summary>
    [Fact]
    public void A_certificate_password_in_the_clear_is_refused()
        => Assert.Contains(Good(new CertificateOptions(Path: "server.pfx", Password: "hunter2")).Problems(),
            p => p.Contains("in the clear"));

    [Fact]
    public void A_certificate_named_twice_is_refused()
        => Assert.Contains(Good(new CertificateOptions(Thumbprint: "AB", Path: "server.pfx")).Problems(), p => p.Contains("both"));

    [Fact]
    public void Settings_survive_the_file_with_comments_and_any_case()
    {
        var path = Path.Combine(_root, "server.json");
        File.WriteAllText(path, """
            {
              // where
              "listen": ["0.0.0.0:8443"],
              "AllowedSubnets": ["10.20.0.0/16"],
              "hostNames": ["enactive.lab.local"],
              "certificate": { "thumbprint": "AB12" },
            }
            """);

        var options = ServerOptions.Load(path, out var problem);

        Assert.Null(problem);
        Assert.Empty(options!.Problems());
        Assert.Equal("AB12", options.Certificate!.Thumbprint);
    }

    [Fact]
    public void No_settings_file_is_said_with_where_it_was_looked_for()
    {
        var path = Path.Combine(_root, "server.json");

        Assert.Null(ServerOptions.Load(path, out var problem));
        Assert.Contains(path, problem);
    }

    // ── the certificate ─────────────────────────────────────────────────────

    private string Pfx(string password, DateTimeOffset from, DateTimeOffset to)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=enactive.lab.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(from, to);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    [Fact]
    public void A_pfx_with_its_protected_password_is_presented()
    {
        var path = Pfx("s3cret", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));

        using var certificate = ServerCertificate.Load(new CertificateOptions(Path: path, Password: Secret.Protect("s3cret")), out var problem);

        Assert.Null(problem);
        Assert.True(certificate!.HasPrivateKey);
    }

    /// <summary>An expired certificate teaches every user to click through the warning - the habit HTTPS must not build.</summary>
    [Fact]
    public void An_expired_certificate_is_refused_with_its_dates()
    {
        var path = Pfx("", DateTimeOffset.Now.AddYears(-2), DateTimeOffset.Now.AddDays(-1));

        Assert.Null(ServerCertificate.Load(new CertificateOptions(Path: path), out var problem));
        Assert.Contains("not now", problem);
    }

    [Fact]
    public void A_certificate_file_that_is_not_there_is_said()
    {
        Assert.Null(ServerCertificate.Load(new CertificateOptions(Path: Path.Combine(_root, "none.pfx")), out var problem));
        Assert.Contains("no certificate file", problem);
    }

    [Fact]
    public void A_thumbprint_the_store_does_not_have_is_said()
    {
        Assert.Null(ServerCertificate.Load(new CertificateOptions(Thumbprint: "00 11 22", Store: "CurrentUser"), out var problem));
        Assert.Contains("No certificate with thumbprint 001122", problem);
    }

    // ── the data folder ─────────────────────────────────────────────────────

    /// <summary>Two services on one profile would run two schedulers over the same schedules.</summary>
    [Fact]
    public void A_second_service_on_the_same_data_folder_is_refused_with_who_has_it()
    {
        var lockFile = DataFolderLock.PathFor(_root);
        using var other = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.DeleteOnClose);
        System.Text.Json.JsonSerializer.Serialize(other, new Enactive.Core.Storage.LeaseHolder("Enactive.Server", 77, "svc-enactive", DateTimeOffset.UtcNow));
        other.Flush();

        Assert.Null(DataFolderLock.TryTake(_root, out var holder));
        Assert.Equal("svc-enactive", holder?.User);
    }

    // ── the pipeline ────────────────────────────────────────────────────────

    /// <summary>The test server leaves the client's address empty; the gate is NetworkGateTests' to test.</summary>
    private sealed class FromInside : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, go) => { context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.0.9"); return go(); });
            next(app);
        };
    }

    private async Task<WebApplication> StartedAsync()
    {
        var app = ServerApp.Build([], Good() with { HostNames = ["localhost"] }, _root, certificate: null, b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddTransient<IStartupFilter, FromInside>();
        });
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Health_says_up_and_which_build_and_nothing_else()
    {
        await using var app = await StartedAsync();

        var response = await app.GetTestClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"version\"", body);
    }

    [Fact]
    public async Task The_sign_in_page_is_served_with_the_app_s_script()
    {
        await using var app = await StartedAsync();

        var page = await app.GetTestClient().GetStringAsync("/signin");

        Assert.Contains("<title>Sign in - Enactive</title>", page);
        Assert.Contains("blazor.web.js", page);
    }
}
