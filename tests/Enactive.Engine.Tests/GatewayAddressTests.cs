namespace Enactive.Engine.Tests;

using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// Turning a typed gateway address into the hub to connect to.
///
/// <para>Small enough to look obviously right and wrong in a way nothing downstream can catch: a
/// hub URL that resolves to somewhere else does not fail as "wrong address", it fails as an
/// authentication error against a stranger's server, and the person reading that message goes
/// looking at their token.</para>
/// </summary>
public sealed class GatewayAddressTests
{
    [Theory]
    [InlineData("https://remote.enactive.dev", "https://remote.enactive.dev/hubs/host")]
    [InlineData("https://remote.enactive.dev/", "https://remote.enactive.dev/hubs/host")]
    [InlineData("  https://remote.enactive.dev  ", "https://remote.enactive.dev/hubs/host")]
    [InlineData("http://localhost:5099", "http://localhost:5099/hubs/host")]
    public void A_gateway_address_becomes_its_hub(string given, string expected)
        => Assert.Equal(expected, GatewayAddress.Hub(given).AbsoluteUri);

    /// <summary>
    /// The decisive one. Uri's relative resolution replaces the last segment of a path that does
    /// not end in a slash, so a gateway hosted under a path resolves to the ROOT of that host - an
    /// address that usually exists and belongs to something else entirely.
    /// </summary>
    [Fact]
    public void A_gateway_under_a_path_keeps_that_path()
        => Assert.Equal(
            "https://example.com/enactive/hubs/host",
            GatewayAddress.Hub("https://example.com/enactive").AbsoluteUri);

    /// <summary>
    /// Refused rather than repaired. Guessing a scheme for somebody who typed a bare host name is
    /// how a device token goes out over http to a machine they did not mean.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("remote.enactive.dev")]
    [InlineData("ftp://remote.enactive.dev")]
    [InlineData("not a url at all")]
    public void An_address_that_is_not_a_gateway_is_refused(string given)
        => Assert.Throws<ArgumentException>(() => GatewayAddress.Hub(given));

    /// <summary>Null is the unconfigured case, and is refused for the same reason as the empty one.</summary>
    [Fact]
    public void No_address_at_all_is_refused()
        => Assert.Throws<ArgumentException>(() => GatewayAddress.Hub(null));
}
