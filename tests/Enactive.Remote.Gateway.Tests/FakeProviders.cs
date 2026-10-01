namespace Enactive.Remote.Gateway.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

/// <summary>What a fake Google id token gets wrong, on purpose.</summary>
public enum IdTokenFault
{
    None,
    WrongSignature,
    WrongAudience,
    WrongIssuer,
    Expired,
    WrongNonce
}

/// <summary>
/// GitHub and Google as far as signing in sees them, in-process: GitHub's authorize redirect, token
/// endpoint and <c>/user</c>; Google's discovery document, signing keys, authorize page and token
/// endpoint. The gateway's handlers reach it through their backchannel; a test plays the browser.
///
/// <para>It checks what the real providers check - the client, the redirect URI, the PKCE verifier
/// against the challenge - because the tests that a stolen code or a forged state gets nobody in are
/// only worth something if the provider at the other end would have refused what the gateway sent.
/// A fake that handed a token to any request would let a gateway that never sent a verifier pass.</para>
/// </summary>
internal sealed partial class FakeProviders : IAsyncDisposable
{
    public const string GitHubBase = "https://github.fake";
    public const string GitHubApi = "https://api.github.fake";
    public const string GoogleAuthority = "https://accounts.google.fake";

    public const string GitHubClientId = "github-client";
    public const string GitHubClientSecret = "github-secret";
    public const string GoogleClientId = "google-client.apps.fake";
    public const string GoogleClientSecret = "google-secret";

    private const string KeyId = "fake-key";

    private readonly WebApplication _app;

    // A key made per instance, so no test can pass on a key another test published.
    private readonly RSA _key = RSA.Create(2048);

    // Another key under the SAME key id: a token signed with it names a key the gateway trusts and
    // is still not signed by it, which is the forgery the signature check exists for.
    private readonly RSA _forger = RSA.Create(2048);

    private readonly ConcurrentDictionary<string, Grant> _codes = new();
    private readonly ConcurrentDictionary<string, GitHubAccount> _tokens = new();

    private FakeProviders(WebApplication app)
    {
        _app = app;
    }

    /// <summary>Who GitHub says signed in, for the next code it issues.</summary>
    public GitHubAccount GitHubUser { get; set; } = new(12345, "octocat");

    /// <summary>Who Google says signed in, for the next code it issues.</summary>
    public GoogleAccount GoogleUser { get; set; } = new("108000000000000000001", "Ann", "ann@example.com");

    /// <summary>What the next id token gets wrong.</summary>
    public IdTokenFault Fault { get; set; }

    /// <summary>Every code, access token and id token handed out: a test checks none of them is logged.</summary>
    public ConcurrentBag<string> Secrets { get; } = [];

    public static async Task<FakeProviders> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        var fake = new FakeProviders(app);
        fake.Map(app);

        await app.StartAsync();
        return fake;
    }

    /// <summary>A handler into this server, for a gateway's backchannel.</summary>
    public HttpMessageHandler CreateHandler() => _app.GetTestServer().CreateHandler();

    /// <summary>The browser's side of the provider: no cookies, no redirects followed.</summary>
    public HttpClient CreateBrowser() => _app.GetTestServer().CreateClient();

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _key.Dispose();
        _forger.Dispose();
    }

    /// <summary>
    /// The form Google's page posts back to the gateway (<c>response_mode=form_post</c>): where it goes,
    /// and its fields.
    /// </summary>
    public static (Uri Action, Dictionary<string, string> Fields) ReadFormPost(string html)
    {
        var action = FormAction().Match(html).Groups[1].Value;
        var fields = HiddenInput().Matches(html)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));

        return (new Uri(WebUtility.HtmlDecode(action)), fields);
    }

    private void Map(WebApplication app)
    {
        // ── GitHub ──────────────────────────────────────────────────────────

        // Straight back to the callback, as GitHub does for an app the person has already authorized.
        app.MapGet("/login/oauth/authorize", (HttpRequest request) =>
        {
            var query = request.Query;

            if (query["client_id"] != GitHubClientId || query["code_challenge_method"] != "S256")
            {
                return Results.BadRequest();
            }

            var code = Issue(new Grant("github", query["redirect_uri"]!, query["code_challenge"]!, null, null, GitHubUser));
            return Results.Redirect(QueryHelpers.AddQueryString(query["redirect_uri"]!,
                new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"] }));
        });

        app.MapPost("/login/oauth/access_token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();

            if (Redeem(form, "github", GitHubClientId, GitHubClientSecret) is not { } grant)
            {
                return Results.Json(new { error = "bad_verification_code" }, statusCode: 400);
            }

            var token = Secret();
            _tokens[token] = grant.GitHub!;
            return Results.Json(new { access_token = token, token_type = "bearer", scope = "" });
        });

        app.MapGet("/user", (HttpRequest request) =>
        {
            var header = request.Headers.Authorization.ToString();

            if (!header.StartsWith("Bearer ", StringComparison.Ordinal)
                || !_tokens.TryGetValue(header["Bearer ".Length..], out var account))
            {
                return Results.Unauthorized();
            }

            return Results.Json(new { id = account.Id, login = account.Login, email = (string?)null });
        });

        // ── Google ──────────────────────────────────────────────────────────

        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = GoogleAuthority,
            ["authorization_endpoint"] = $"{GoogleAuthority}/o/oauth2/v2/auth",
            ["token_endpoint"] = $"{GoogleAuthority}/token",
            ["jwks_uri"] = $"{GoogleAuthority}/oauth2/v3/certs",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" }
        }));

        app.MapGet("/oauth2/v3/certs", () =>
        {
            var key = _key.ExportParameters(includePrivateParameters: false);
            return Results.Json(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA", use = "sig", alg = "RS256", kid = KeyId,
                        n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent)
                    }
                }
            });
        });

        // The page Google shows answers with a form the browser posts to the callback, which is what
        // the OpenID Connect handler asks for by default.
        app.MapGet("/o/oauth2/v2/auth", (HttpRequest request) =>
        {
            var query = request.Query;

            if (query["client_id"] != GoogleClientId || query["response_type"] != "code"
                || query["code_challenge_method"] != "S256" || query["response_mode"] != "form_post")
            {
                return Results.BadRequest();
            }

            var code = Issue(new Grant(
                "google", query["redirect_uri"]!, query["code_challenge"]!, query["nonce"], GoogleUser, null));

            var html = new StringBuilder()
                .Append($"<form method=\"post\" action=\"{WebUtility.HtmlEncode(query["redirect_uri"])}\">")
                .Append($"<input type=\"hidden\" name=\"code\" value=\"{WebUtility.HtmlEncode(code)}\"/>")
                .Append($"<input type=\"hidden\" name=\"state\" value=\"{WebUtility.HtmlEncode(query["state"])}\"/>")
                .Append("</form>")
                .ToString();

            return Results.Content(html, "text/html");
        });

        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();

            if (Redeem(form, "google", GoogleClientId, GoogleClientSecret) is not { } grant)
            {
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            }

            var idToken = IdToken(grant);
            var accessToken = Secret();
            Secrets.Add(idToken);
            Secrets.Add(accessToken);

            return Results.Json(new
            {
                access_token = accessToken, id_token = idToken, token_type = "Bearer", expires_in = 3600
            });
        });
    }

    /// <summary>The id token for a redeemed grant, with whatever <see cref="Fault"/> says is wrong with it.</summary>
    private string IdToken(Grant grant)
    {
        var now = DateTime.UtcNow;
        var expired = Fault == IdTokenFault.Expired;
        var account = grant.Google!;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Fault == IdTokenFault.WrongIssuer ? "https://accounts.elsewhere.fake" : GoogleAuthority,
            Audience = Fault == IdTokenFault.WrongAudience ? "another-client.apps.fake" : GoogleClientId,
            IssuedAt = expired ? now.AddHours(-2) : now,
            NotBefore = expired ? now.AddHours(-2) : now,
            Expires = expired ? now.AddHours(-1) : now.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = account.Sub,
                ["name"] = account.Name,
                ["email"] = account.Email,
                ["nonce"] = Fault == IdTokenFault.WrongNonce ? "a-nonce-of-another-sign-in" : grant.Nonce!
            },
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(Fault == IdTokenFault.WrongSignature ? _forger : _key) { KeyId = KeyId },
                SecurityAlgorithms.RsaSha256)
        });
    }

    private string Issue(Grant grant)
    {
        var code = Secret();
        _codes[code] = grant;
        return code;
    }

    /// <summary>
    /// The grant a token request redeems, once, or null when anything about the request is not what the
    /// grant was issued for. The verifier is checked against the challenge as S256 says: a code is only
    /// worth something to the client that started the sign-in it was issued to.
    /// </summary>
    private Grant? Redeem(IFormCollection form, string provider, string clientId, string clientSecret)
    {
        if (!_codes.TryRemove(form["code"].ToString(), out var grant))
        {
            return null;
        }

        var verifier = form["code_verifier"].ToString();
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return grant.Provider == provider
            && form["client_id"] == clientId && form["client_secret"] == clientSecret
            && form["redirect_uri"] == grant.RedirectUri
            && verifier.Length > 0 && challenge == grant.Challenge
                ? grant
                : null;
    }

    private string Secret()
    {
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        Secrets.Add(secret);
        return secret;
    }

    [GeneratedRegex("<form method=\"post\" action=\"([^\"]*)\">")]
    private static partial Regex FormAction();

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\"/>")]
    private static partial Regex HiddenInput();

    private sealed record Grant(
        string Provider, string RedirectUri, string Challenge, string? Nonce,
        GoogleAccount? Google, GitHubAccount? GitHub);
}

internal sealed record GitHubAccount(long Id, string Login);

internal sealed record GoogleAccount(string Sub, string Name, string Email);
