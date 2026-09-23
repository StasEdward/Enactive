namespace Enactive.Engine.Tests;

using Enactive.Settings;
using Xunit;

/// <summary>
/// Where a provider runs, for the panel that answers "how much of this run did we BUY".
///
/// <para><b>What was wrong.</b> The rule was a substring search —
/// <c>url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("[::1]")</c> — which
/// reads an Ollama at <c>http://192.168.1.50:11434</c> as a cloud provider and files free tokens
/// under money spent, and reads <c>https://localhost.example.com</c>, a perfectly public host, as
/// local. A URL has a host field; asking for it is no harder than searching the whole string and
/// cannot be fooled by either.</para>
///
/// <para>The line is not "this exact machine". A model answering on the box under your desk was not
/// bought either, and putting its tokens in the paid column is the one answer that is certainly
/// wrong.</para>
/// </summary>
public sealed class WhoDidTheWorkTests
{
    [Theory]
    // The four in this workspace's own settings, which is where the question came from.
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1337/v1", true)]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    [InlineData("http://localhost:1234/v1", true)]
    [InlineData("https://api.anthropic.com", false)]
    [InlineData("https://api.deepseek.com", false)]

    // The blind spot: a model on this network, which the substring rule called cloud.
    [InlineData("http://192.168.1.50:11434", true)]
    [InlineData("http://10.0.0.8:8080/v1", true)]
    [InlineData("http://172.16.4.2:1234", true)]
    [InlineData("http://172.32.4.2:1234", false)]   // outside 172.16/12 - a public address
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("http://workshop.local:11434", true)]
    [InlineData("localhost:11434", true)]           // no scheme is still a host and a port

    // The other direction, which the substring rule got wrong in the expensive way: a PUBLIC host
    // whose name merely contains the word.
    [InlineData("https://localhost.example.com/v1", false)]
    [InlineData("https://127.0.0.1.nip.io/v1", false)]

    // Cannot tell is not local. A rule that guesses here makes the numbers look better than they
    // are, which is the only mistake this panel must not make.
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url at all", false)]
    public void An_address_that_costs_nothing_is_read_as_local(string? baseUrl, bool expected)
        => Assert.Equal(expected, ProviderReach.Local(baseUrl));

    /// <summary>
    /// And this machine by its own name — what a person types when the model runs here but the
    /// endpoint was written down from another box.
    /// </summary>
    [Fact]
    public void This_machine_by_name_is_this_machine()
    {
        var me = Environment.MachineName;

        Assert.True(ProviderReach.Local($"http://{me}:11434/v1"));
        Assert.True(ProviderReach.Local($"http://{me.ToLowerInvariant()}.local:11434"));
        Assert.False(ProviderReach.Local($"https://{me}.example.com/v1"));
    }
}
