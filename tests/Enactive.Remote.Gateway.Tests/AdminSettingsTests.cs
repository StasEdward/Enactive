namespace Enactive.Remote.Gateway.Tests;

using System.Security.Claims;
using Enactive.Remote.Gateway.Administration;
using Microsoft.Extensions.Configuration;

public sealed class AdminSettingsTests
{
    [Fact]
    public void Administration_is_disabled_without_configuration()
        => Assert.Null(AdminSettings.Read(new ConfigurationBuilder().Build(), null));

    [Theory]
    [InlineData(AdminSettings.OriginKey, "")]
    [InlineData(AdminSettings.AuthorityKey, "")]
    [InlineData(AdminSettings.ClientIdKey, "")]
    [InlineData(AdminSettings.ClientSecretKey, "")]
    [InlineData(AdminSettings.MfaAcrKey, "")]
    [InlineData(AdminSettings.OriginKey, "http://admin.example.test")]
    [InlineData(AdminSettings.OriginKey, "https://admin.example.test/path")]
    [InlineData(AdminSettings.OriginKey, "https://panel.example.test:8443")]
    [InlineData(AdminSettings.AuthorityKey, "http://issuer.example.test")]
    [InlineData(AdminSettings.MfaAcrKey, "password mfa")]
    public void Partial_or_unsafe_settings_refuse_startup(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => AdminSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new Uri("https://panel.example.test")));
    }

    [Fact]
    public void Valid_settings_preserve_issuer_and_secret_exactly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        var settings = AdminSettings.Read(config, new Uri("https://panel.example.test"))!;
        Assert.Equal("https://issuer.example.test/tenant", settings.Authority);
        Assert.Equal(" secret ", settings.ClientSecret);
    }

    [Theory]
    [InlineData("acr", "mfa")]
    [InlineData("iss", "https://issuer.example.test")]
    [InlineData("sub", "a")]
    [InlineData("auth_time", "0")]
    public void Duplicate_security_claims_are_rejected(string claim, string value)
    {
        var now = DateTimeOffset.UtcNow;
        Claim[] claims = [new("acr", "mfa"), new("iss", "https://issuer.example.test"), new("sub", "a"),
            new("auth_time", now.ToUnixTimeSeconds().ToString())];
        Assert.True(AdminAuthentication.Evidence(new ClaimsPrincipal(new ClaimsIdentity(claims)), "mfa", now, out _, out _));
        Assert.False(AdminAuthentication.Evidence(new ClaimsPrincipal(new ClaimsIdentity(claims.Append(new Claim(claim, value)))), "mfa", now, out _, out _));
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        [AdminSettings.OriginKey] = "https://admin.example.test",
        [AdminSettings.AuthorityKey] = "https://issuer.example.test/tenant",
        [AdminSettings.ClientIdKey] = "client",
        [AdminSettings.ClientSecretKey] = " secret ",
        [AdminSettings.MfaAcrKey] = "mfa"
    };
}
