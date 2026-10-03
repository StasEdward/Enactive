namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enactive.Remote.Gateway.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// What every response tells the browser about itself, and the published list of what the panel is.
///
/// <para>The headers are the part of the gateway's security that no endpoint test notices: each of
/// them can disappear - from one kind of response, or from all of them - and every request still
/// succeeds. So they are asked for on each KIND of response the gateway gives, not just the page.</para>
/// </summary>
public sealed class HeadersTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    /// <summary>The policy, directive by directive. Order is not part of it; every directive is.</summary>
    private static readonly string[] Policy =
    [
        "default-src 'self'", "script-src 'self'", "style-src 'self'", "font-src 'self'", "img-src 'self'",
        "connect-src 'self'", "object-src 'none'", "worker-src 'none'", "manifest-src 'none'",
        "frame-ancestors 'none'", "base-uri 'none'", "form-action 'self'",
    ];

    private WebApplicationFactory<Program> _gateway = null!;
    private HttpClient _http = null!;

    public Task InitializeAsync()
    {
        // GitHub configured, so its start is a real redirect to the provider rather than a 404: a
        // redirect is answered by the authentication handler, which is a different writer of
        // responses from every other one here.
        _gateway = TestGateway.Create(database, configure: builder =>
        {
            builder.UseSetting(ExternalProviders.PublicOriginSetting, "https://remote.example.test");
            builder.UseSetting("ENACTIVE_GITHUB_CLIENT_ID", "github-client");
            builder.UseSetting("ENACTIVE_GITHUB_CLIENT_SECRET", "github-secret");
        });

        // Not following redirects: the response under test is the redirect itself.
        _http = _gateway.CreateDefaultClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _http.Dispose();
        return _gateway.DisposeAsync().AsTask();
    }

    /// <summary>
    /// The page, a script the page names, a module only a script imports, the API signed out and
    /// refused, the provider redirect, a policy page, a path that does not exist, and the manifest -
    /// one of each writer of responses. The status is asserted too, so a row cannot pass by having
    /// quietly become a 404.
    ///
    /// <para>Shown red by removing the new headers and directives from the middleware in
    /// <c>Program</c>.</para>
    /// </summary>
    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData("/app.js", HttpStatusCode.OK)]
    [InlineData("/js/api.js", HttpStatusCode.OK)]
    [InlineData("/api/session", HttpStatusCode.OK)]
    [InlineData("/api/state", HttpStatusCode.Unauthorized)]
    [InlineData("/auth/github/start", HttpStatusCode.Redirect)]
    [InlineData("/privacy.html", HttpStatusCode.OK)]
    [InlineData("/terms.html", HttpStatusCode.OK)]
    [InlineData("/no-such-page", HttpStatusCode.NotFound)]
    [InlineData("/.well-known/enactive-panel.json", HttpStatusCode.OK)]
    public async Task Every_response_carries_the_security_headers(string path, HttpStatusCode status)
    {
        using var response = await _http.GetAsync(path);

        Assert.Equal(status, response.StatusCode);
        AssertSecurityHeaders(response);
    }

    /// <summary>
    /// The two refusals the middleware writes itself, from what it caught: a <see cref="GatewayFault"/> an
    /// endpoint threw, and the antiforgery check's exception. They are written by a catch, after the request
    /// went down the pipeline and failed - a different path from every row above, and the one a change to
    /// the middleware is likeliest to leave without the headers.
    /// </summary>
    [Fact]
    public async Task A_refusal_written_from_a_caught_exception_carries_the_security_headers()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, "headers-" + Guid.NewGuid().ToString("N")[..8]);

        // Thrown by the endpoint: a cursor that is not one.
        using (var fault = await browser.SendAsync(HttpMethod.Post, "/api/notices/read", new { through = "not-a-cursor" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, fault.StatusCode);
            Assert.DoesNotContain("\"csrf\"", await fault.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            AssertSecurityHeaders(fault);
        }

        // Thrown by the antiforgery check: the same call without its token.
        using (var csrf = await browser.SendAsync(HttpMethod.Post, "/api/notices/read", new { through = "x" }, csrf: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
            Assert.Contains("\"csrf\"", await csrf.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            AssertSecurityHeaders(csrf);
        }
    }

    /// <summary>
    /// A HEAD of the manifest is answered as its GET is, headers and all. A client that checks whether the
    /// list exists - or what its validators are - before fetching it met a 404 from the static files, which
    /// reads as a gateway that publishes no list.
    /// </summary>
    [Fact]
    public async Task A_head_of_the_manifest_is_answered_like_its_get()
    {
        using var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/.well-known/enactive-panel.json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        AssertSecurityHeaders(response);
    }

    /// <summary>
    /// The manifest names every script and stylesheet the gateway serves - the modules the page never
    /// names included, since an altered module is as able to read a key as an altered page script -
    /// each with the SHA-256 of its bytes, and nothing else.
    ///
    /// <para>Checked against both ends: the files in the web root, walked here independently of the
    /// gateway's own walk, and the bytes actually served at each path. A list that matched the disk
    /// and not what is served would be a list of the wrong thing.</para>
    /// </summary>
    [Fact]
    public async Task The_panel_manifest_lists_every_script_with_its_hash()
    {
        using var response = await _http.GetAsync("/.well-known/enactive-panel.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());

        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("sha256", manifest.RootElement.GetProperty("algorithm").GetString());

        var listed = manifest.RootElement.GetProperty("files").EnumerateObject()
            .ToDictionary(file => file.Name, file => file.Value.GetString()!, StringComparer.Ordinal);

        var webRoot = _gateway.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var onDisk = Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file) is ".js" or ".mjs" or ".css")
            .ToDictionary(
                file => "/" + Path.GetRelativePath(webRoot, file).Replace(Path.DirectorySeparatorChar, '/'),
                file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal);

        // The walk reaches every kind of place a script or stylesheet lives, or this test proves little.
        Assert.Contains("/app.js", onDisk.Keys);
        Assert.Contains("/js/api.js", onDisk.Keys);
        Assert.Contains("/vendor/qrcode.mjs", onDisk.Keys);
        Assert.Contains("/brand/brand.css", onDisk.Keys);

        Assert.Equal(onDisk.OrderBy(file => file.Key, StringComparer.Ordinal),
            listed.OrderBy(file => file.Key, StringComparer.Ordinal));

        foreach (var (path, hash) in listed)
        {
            var served = await _http.GetByteArrayAsync(path);

            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(served)));
        }
    }

    /// <summary>
    /// The same release publishes the same bytes, on any server: keys in ordinal order and "\n" line
    /// ends, whatever the operating system. A list that came out in another order, or with "\r\n" on a
    /// Windows server, would hash differently from the same list elsewhere - and comparing the list
    /// itself, as a whole, is the simplest check anyone can make. The other tests sort before they
    /// compare, so only this one reads the raw text.
    /// </summary>
    [Fact]
    public async Task The_manifest_is_the_same_bytes_on_any_server()
    {
        var raw = await _http.GetStringAsync("/.well-known/enactive-panel.json");

        Assert.DoesNotContain("\r", raw, StringComparison.Ordinal);
        Assert.Contains("\n", raw, StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(raw);
        var order = manifest.RootElement.GetProperty("files").EnumerateObject().Select(file => file.Name).ToList();

        Assert.Equal(order.Order(StringComparer.Ordinal), order);
    }

    /// <summary>
    /// The manifest is made from the files when the gateway starts, not kept beside them: change a script
    /// and the next start lists the new hash. A list written once and shipped would go on vouching for the
    /// previous release - the one thing it exists to tell apart. Shown on a web root of the test's own, so
    /// it can be changed, with a stylesheet, a module in a folder, and files that are neither, which the
    /// list leaves out.
    /// </summary>
    [Fact]
    public void The_manifest_changes_when_a_script_changes()
    {
        var webRoot = Directory.CreateTempSubdirectory("enactive-panel-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<script src=\"/app.js\" type=\"module\"></script>");
            File.WriteAllText(Path.Combine(webRoot, "app.js"), "export const release = 1;");
            File.WriteAllText(Path.Combine(webRoot, "app.css"), "body { margin: 0; }");
            Directory.CreateDirectory(Path.Combine(webRoot, "vendor"));
            File.WriteAllText(Path.Combine(webRoot, "vendor", "lib.mjs"), "export default 1;");
            File.WriteAllText(Path.Combine(webRoot, "mark.svg"), "<svg/>");
            File.WriteAllText(Path.Combine(webRoot, "privacy.html"), "<p>Privacy</p>");

            var before = PanelAssets.Load(webRoot).Files;

            Assert.Equal(["/app.css", "/app.js", "/vendor/lib.mjs"], before.Keys.Order(StringComparer.Ordinal));

            File.WriteAllText(Path.Combine(webRoot, "app.js"), "export const release = 2;");

            var after = PanelAssets.Load(webRoot).Files;

            Assert.NotEqual(before["/app.js"], after["/app.js"]);
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("export const release = 2;"))),
                after["/app.js"]);
            Assert.Equal(before["/app.css"], after["/app.css"]);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            Policy.Order(StringComparer.Ordinal),
            Header(response, "Content-Security-Policy").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.Ordinal));
        Assert.Equal("camera=(), microphone=(), geolocation=(), payment=()", Header(response, "Permissions-Policy"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Resource-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
    }

    /// <summary>One value of a response header, wherever HttpClient filed it.</summary>
    private static string Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : response.Content.Headers.TryGetValues(name, out var content)
                ? string.Join(", ", content)
                : throw new Xunit.Sdk.XunitException($"{response.RequestMessage?.RequestUri} has no {name} header.");
}
