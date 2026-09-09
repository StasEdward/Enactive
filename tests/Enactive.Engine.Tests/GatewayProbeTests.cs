namespace Enactive.Engine.Tests;

using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// What "Test connection" answers WITHOUT a gateway.
///
/// <para>Here rather than beside the checks that need a real server, because these are the cases
/// that must never reach one. A test that needed a database to prove that an empty address is
/// refused would be unable to run on a machine with no database - which is every machine where
/// somebody is about to type an address wrong.</para>
/// </summary>
public sealed class GatewayProbeTests
{
    /// <summary>
    /// An address that is not a gateway address is refused before anything is sent. The token is a
    /// bearer credential, and a check that dialled whatever a typo resolves to would hand it to a
    /// stranger to find out they were a stranger.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("remote.enactive.dev")]
    [InlineData("ftp://remote.enactive.dev")]
    public async Task An_address_that_is_not_a_gateway_is_refused_without_connecting(string address)
    {
        var check = await GatewayProbe.CheckAsync(address, "a-token", []);

        Assert.False(check.Reached);
        Assert.Contains("http", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Settings with no token say so, rather than failing later as a connection problem. The two
    /// look identical from the outside and have completely different fixes.
    /// </summary>
    [Fact]
    public async Task No_token_is_reported_as_no_token_rather_than_as_a_connection_failure()
    {
        var check = await GatewayProbe.CheckAsync("https://remote.enactive.dev", "", []);

        Assert.False(check.Reached);
        Assert.Contains("no device token", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The order matters: a bad address is reported as a bad address even when the token is also
    /// missing. Reporting the token first would send somebody to the gateway to reissue a token
    /// that was never the problem.
    /// </summary>
    [Fact]
    public async Task A_bad_address_is_reported_before_a_missing_token()
    {
        var check = await GatewayProbe.CheckAsync("not a url", "", []);

        Assert.False(check.Reached);
        Assert.DoesNotContain("no device token", check.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
