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

    /// <summary>
    /// Hello comes before Sync, and a gateway of another protocol is reported in its own words.
    /// Syncing first would publish workspace names sealed in a format that gateway's browsers may not
    /// open, and the check would then report whatever Sync happened to say instead of the reason.
    /// </summary>
    [Fact]
    public async Task A_gateway_of_another_protocol_is_reported_in_its_own_words_before_anything_is_published()
    {
        const string words = "This computer and the service speak different versions - update Enactive.";
        var gateway = new FakeGateway
        {
            HelloRefusal = new GatewayRefusedException(Remote.Contracts.FaultCode.ProtocolMismatch, words)
        };

        var check = await GatewayProbe.CheckAsync(gateway, [], CancellationToken.None);

        Assert.False(check.Reached);
        Assert.Contains(words, check.Detail, StringComparison.Ordinal);
        Assert.Equal(["Hello"], gateway.Calls);
    }

    /// <summary>On a gateway that speaks this protocol the check says hello and then syncs once.</summary>
    [Fact]
    public async Task A_check_says_hello_and_then_syncs()
    {
        var gateway = new FakeGateway();

        var check = await GatewayProbe.CheckAsync(gateway, [], CancellationToken.None);

        Assert.True(check.Reached, check.Detail);
        Assert.Equal(["Hello", "Sync"], gateway.Calls);
    }
}
