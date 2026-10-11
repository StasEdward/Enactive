namespace Enactive.Server.Tests;

using System.Net;
using System.Text.RegularExpressions;
using Enactive.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Signing in through the service's real pipeline (server plan 1.3): every page is behind it, a right password gets a
/// session, a session ends when its account is disabled - on its next request, not when the cookie runs out.
/// </summary>
public sealed partial class SignInTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-signin", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private UserStore _users = null!;

    private const string Password = "correct horse battery";

    private sealed class FromInside : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, go) => { context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.0.9"); return go(); });
            next(app);
        };
    }

    public async Task InitializeAsync()
    {
        var options = new ServerOptions
        {
            Listen = ["0.0.0.0:8443"], AllowedSubnets = ["10.20.0.0/16"], HostNames = ["localhost"],
            Certificate = new CertificateOptions(Path: "server.pfx")
        };
        _app = ServerApp.Build([], options, _root, certificate: null, b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddTransient<IStartupFilter, FromInside>();
        });
        await _app.StartAsync();
        _users = _app.Services.GetRequiredService<UserStore>();
        _users.Add("alice", Password, Role.Operator);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>A browser enough for these tests: it keeps cookies and does not follow redirects, so they can be read.</summary>
    private sealed class Browser(HttpClient client)
    {
        private readonly Dictionary<string, string> _cookies = new();

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (_cookies.Count > 0)
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var cookie in set)
                {
                    var pair = cookie.Split(';')[0].Split('=', 2);
                    if (pair[1].Length == 0 || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                        _cookies.Remove(pair[0]);
                    else
                        _cookies[pair[0]] = pair[1];
                }
            return response;
        }

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

        public Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form)
            => SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) });

        public IReadOnlyCollection<string> Cookies => _cookies.Keys;
    }

    private Browser NewBrowser()
    {
        var client = _app.GetTestServer().CreateClient();
        client.BaseAddress = new Uri("https://localhost/");
        return new Browser(client);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static async Task<string> TokenFromAsync(HttpResponseMessage page)
        => Token().Match(await page.Content.ReadAsStringAsync()).Groups[1].Value;

    private static async Task<HttpResponseMessage> SignInAsync(Browser browser, string name, string password)
    {
        var token = await TokenFromAsync(await browser.GetAsync("/signin"));
        return await browser.PostAsync("/signin", new()
        {
            ["_handler"] = "signin",
            ["__RequestVerificationToken"] = token,
            ["Input.Name"] = name,
            ["Input.Password"] = password
        });
    }

    [Fact]
    public async Task A_page_sends_somebody_not_signed_in_to_sign_in_and_back()
    {
        var response = await NewBrowser().GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/signin?ReturnUrl=%2F", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_right_password_gives_a_session_that_opens_the_pages()
    {
        var browser = NewBrowser();

        var signedIn = await SignInAsync(browser, "alice", Password);

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains("enactive", browser.Cookies);
        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("alice", await home.Content.ReadAsStringAsync());
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "signed in", Remote: "10.20.0.9" });
    }

    [Fact]
    public async Task The_session_cookie_is_secure_http_only_and_strict()
    {
        var browser = NewBrowser();
        var signedIn = await SignInAsync(browser, "alice", Password);

        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("enactive="));

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_password_gets_no_session_and_one_sentence()
    {
        var browser = NewBrowser();

        var response = await SignInAsync(browser, "alice", "not the password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("the name or the password is not right", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("enactive", browser.Cookies);
    }

    /// <summary>Disabled while signed in: the very next request is a signed-out one, not the next morning's.</summary>
    [Fact]
    public async Task A_session_ends_on_its_next_request_once_its_account_is_disabled()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        _users.SetDisabled("alice", true);

        var response = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/signin", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_lowered_role_is_lowered_now()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        _users.SetRole("alice", Role.Viewer);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
    }

    /// <summary>Signing out is a form post of the page itself; a post without its token is refused.</summary>
    [Fact]
    public async Task Signing_out_needs_the_page_s_token()
    {
        var browser = NewBrowser();
        await SignInAsync(browser, "alice", Password);

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/signout", new())).StatusCode);

        var token = await TokenFromAsync(await browser.GetAsync("/"));
        var out_ = await browser.PostAsync("/signout", new() { ["__RequestVerificationToken"] = token });
        Assert.Equal(HttpStatusCode.Redirect, out_.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
    }

    /// <summary>Only a path on this site: a link that says where to go after sign-in cannot send the person elsewhere.</summary>
    [Fact]
    public async Task After_sign_in_the_person_is_sent_only_somewhere_on_this_site()
    {
        var browser = NewBrowser();
        var token = await TokenFromAsync(await browser.GetAsync("/signin"));

        var response = await browser.PostAsync("/signin?ReturnUrl=%2F%2Fevil.example", new()
        {
            ["_handler"] = "signin", ["__RequestVerificationToken"] = token,
            ["Input.Name"] = "alice", ["Input.Password"] = Password
        });

        Assert.Equal("/", response.Headers.Location!.OriginalString.Replace("https://localhost", ""));
    }

    [Fact]
    public async Task Health_answers_without_signing_in()
        => Assert.Equal(HttpStatusCode.OK, (await NewBrowser().GetAsync("/health")).StatusCode);
}
