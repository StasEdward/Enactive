namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Accounts;
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
        Http = gateway.CreateDefaultClient(new CookieContainerHandler(Cookies));
    }

    public CookieContainer Cookies { get; } = new();

    public HttpClient Http { get; }

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
