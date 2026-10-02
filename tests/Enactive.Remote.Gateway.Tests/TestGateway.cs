namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

/// <summary>The real gateway, in-process, on a test class's own database.</summary>
internal static class TestGateway
{
    /// <param name="devSignIn">
    /// Whether the development sign-in is switched on. It is how every HTTP test signs in, so it is on
    /// unless a test is about its absence.
    /// </param>
    public static WebApplicationFactory<Program> Create(
        TestDatabase database, bool devSignIn = true, Action<IWebHostBuilder>? configure = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ENACTIVE_REMOTE_DB", database.ConnectionString);
            builder.UseSetting("ENACTIVE_DATA", Path.Combine(Path.GetTempPath(), database.Name));

            // Development: the cookie is not marked Secure, which an in-process test server cannot
            // satisfy, and the development sign-in is allowed to exist at all. Everything else about
            // the pipeline is what production runs.
            builder.UseSetting("environment", "Development");

            if (devSignIn)
            {
                builder.UseSetting(DevelopmentSignIn.Setting, "true");
            }

            configure?.Invoke(builder);
        });
}

/// <summary>What <c>GET /api/session</c> answers. Every field, because the wire refuses unknown ones.</summary>
internal sealed record SessionView(bool Authenticated, string CsrfToken, SessionUser? User);

internal sealed record SessionUser(string Id, string DisplayName);

/// <summary>
/// One browser: its own cookie jar and its own antiforgery token, signed in through the development
/// endpoint the way the panel signs in through a provider.
///
/// <para>The jar is held here rather than inside the client so a test can copy a cookie out of it - a
/// cookie somebody kept after they were signed out is exactly what the server-side check exists to
/// refuse, and it cannot be tested without one.</para>
/// </summary>
internal sealed class PanelClient : IDisposable
{
    private string _csrf = "";

    public PanelClient(WebApplicationFactory<Program> gateway)
    {
        Http = gateway.CreateDefaultClient(new NamesItsDevice(this), new CookieContainerHandler(Cookies));
    }

    public CookieContainer Cookies { get; } = new();

    public HttpClient Http { get; }

    /// <summary>
    /// The device this browser names on every private call, as the panel names its own: the first one it
    /// registered, or one registered for it before its first call that needs one. Null until then.
    /// </summary>
    public string? DeviceId { get; private set; }

    /// <summary>A request option that sends it without the device header, for a test of what that is answered.</summary>
    private static readonly HttpRequestOptionsKey<bool> NoDevice = new("enactive-no-device");

    /// <summary>For <see cref="SendAsync"/>'s <c>configure</c>: this request names no device.</summary>
    public static void WithoutDevice(HttpRequestMessage request) => request.Options.Set(NoDevice, true);

    /// <summary>
    /// This browser's device, registered now if it has none yet - for a test that must not have it registered in
    /// the middle of what it counts.
    /// </summary>
    public async Task<string> EnsureDeviceAsync()
    {
        if (DeviceId is null)
        {
            using var key = Enactive.Remote.Contracts.Crypto.P256.Generate();
            await PostAsync("/api/devices", new
            {
                publicKey = Enactive.Remote.Contracts.Crypto.B64.Url(Enactive.Remote.Contracts.Crypto.P256.PublicRaw(key)),
                label = "Test browser"
            });
        }

        return DeviceId!;
    }

    /// <summary>The account this browser signed in to; empty until it has.</summary>
    public string UserId { get; private set; } = "";

    public static async Task<PanelClient> SignedInAsync(WebApplicationFactory<Program> gateway, string name)
    {
        var client = new PanelClient(gateway);
        await client.SignInAsync(name);
        return client;
    }

    /// <summary>
    /// Signs in as <paramref name="name"/>, and takes a fresh antiforgery token afterwards: the token
    /// is bound to who is signed in, so the one fetched before signing in is refused after it.
    /// </summary>
    public async Task SignInAsync(string name)
    {
        await SessionAsync();

        using var response = await SendAsync(HttpMethod.Post, "/api/dev/sign-in", new { name });
        response.EnsureSuccessStatusCode();

        UserId = (await SessionAsync()).User?.Id
            ?? throw new InvalidOperationException("Signed in, and the session does not say as whom.");
    }

    /// <summary>The session as the server sees it now. Refreshes the antiforgery token as a side effect.</summary>
    public async Task<SessionView> SessionAsync()
    {
        var session = (await Http.GetFromJsonAsync<SessionView>("/api/session", RemoteJson.Options))!;
        _csrf = session.CsrfToken;
        return session;
    }

    /// <summary>A request with this browser's antiforgery token, unless the test says otherwise.</summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body = null, bool csrf = true,
        Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: RemoteJson.Options);
        }

        if (csrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", _csrf);
        }

        configure?.Invoke(request);
        return Http.SendAsync(request);
    }

    public async Task<T> GetAsync<T>(string path)
        => (await Http.GetFromJsonAsync<T>(path, RemoteJson.Options))!;

    public async Task PostAsync(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body);
        await EnsureSuccessAsync(response);
    }

    public async Task<T> PostAsync<T>(string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options))!;
    }

    public void Dispose() => Http.Dispose();

    /// <summary>
    /// The calls a browser makes before it has a device to name: the gateway refuses every other private call
    /// that names none (Program.cs, the device filter on the person's API).
    /// </summary>
    private static string PathOf(HttpRequestMessage request)
        => request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.AbsolutePath
            : request.RequestUri?.OriginalString.Split('?')[0] ?? "";

    private static bool NeedsDevice(HttpRequestMessage request)
    {
        var path = PathOf(request);

        if (!path.StartsWith("/api/", StringComparison.Ordinal)) return false;
        if (path is "/api/session" or "/api/providers" or "/api/dev/sign-in" or "/api/logout" or "/api/logout-all") return false;
        return !(request.Method == HttpMethod.Post && path == "/api/devices");
    }

    /// <summary>
    /// Adds the device header to every private call that does not carry one, as the panel's api.js does. A test
    /// that names a device itself, or asks for none (<see cref="WithoutDevice"/>), is left as it is. A device
    /// registered through this browser becomes its own, as the panel's first registration does.
    /// </summary>
    private sealed class NamesItsDevice(PanelClient browser) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var asksForNone = request.Options.TryGetValue(NoDevice, out var none) && none;

            if (!asksForNone && NeedsDevice(request) && !request.Headers.Contains(DeviceHeader.Name))
            {
                // A browser that is not signed in has no device to register, and is answered 401 without one.
                browser.DeviceId ??= await RegisterAsync(request.RequestUri!, ct);
                if (browser.DeviceId is not null)
                {
                    request.Headers.Add(DeviceHeader.Name, browser.DeviceId);
                }
            }

            var response = await base.SendAsync(request, ct);

            if (browser.DeviceId is null && response.IsSuccessStatusCode && request.Method == HttpMethod.Post
                && PathOf(request) == "/api/devices")
            {
                // Buffered and read as text, so the test reads the same answer after this has.
                await response.Content.LoadIntoBufferAsync(ct);
                var registered = RemoteJson.Deserialize<NewDeviceId>(await response.Content.ReadAsStringAsync(ct));
                browser.DeviceId = registered.Id;
            }

            return response;
        }

        private async Task<string?> RegisterAsync(Uri asked, CancellationToken ct)
        {
            using var key = Enactive.Remote.Contracts.Crypto.P256.Generate();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(asked, "/api/devices"))
            {
                Content = JsonContent.Create(new
                {
                    publicKey = Enactive.Remote.Contracts.Crypto.B64.Url(Enactive.Remote.Contracts.Crypto.P256.PublicRaw(key)),
                    label = "Test browser"
                }, options: RemoteJson.Options)
            };
            // A browser made from a copied cookie has not asked for its token yet.
            if (browser._csrf.Length == 0)
            {
                using var session = await base.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(asked, "/api/session")), ct);
                browser._csrf = RemoteJson.Deserialize<SessionView>(await session.Content.ReadAsStringAsync(ct)).CsrfToken;
            }

            request.Headers.Add("X-CSRF-TOKEN", browser._csrf);

            using var response = await base.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"This test browser could not register a device: {(int)response.StatusCode} "
                    + await response.Content.ReadAsStringAsync(ct));
            }

            return RemoteJson.Deserialize<NewDeviceId>(await response.Content.ReadAsStringAsync(ct)).Id;
        }
    }

    private sealed record NewDeviceId(string Id);

    /// <summary>
    /// As EnsureSuccessStatusCode, with the gateway's answer in the message: a bare "400" sends
    /// whoever reads the failure to the debugger for what the response already said.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: "
                + await response.Content.ReadAsStringAsync());
        }
    }
}
