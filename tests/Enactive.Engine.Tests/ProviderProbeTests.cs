namespace Enactive.Engine.Tests;

using System.Net;
using System.Net.Http;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// The Test button, and the light on a provider card.
///
/// <para>The question it has to answer is not "is the server up". Reaching a server proves it is
/// running and that the credential was accepted, and says nothing about the MODEL - which is the
/// commonest way a working setup stops working: a name changed, or a machine where it was never
/// pulled. A light that went green on "the server answered" would be green in exactly that
/// case.</para>
/// </summary>
public sealed class ProviderProbeTests
{
    private sealed class Canned(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class Refuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException(
                HttpRequestError.ConnectionError, "whatever the OS said",
                new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.ConnectionRefused));
    }

    private const string Ollama = """
        {"models":[{"name":"qwen2.5-coder:14b"},{"name":"gemma4:12b"}]}
        """;

    private static Task<ProviderStatus> Check(HttpMessageHandler handler, string? model)
        => ProviderProbe.CheckAsync(
            new HttpClient(handler), ProviderKind.OllamaNative, "ollama",
            "http://localhost:11434/v1", null, null, model);

    [Fact]
    public async Task A_provider_that_answers_and_has_the_model_is_ready()
    {
        var status = await Check(new Canned(Ollama), "gemma4:12b");

        Assert.Equal(ProviderHealth.Ready, status.Health);
        Assert.Contains("gemma4:12b", status.Summary);
    }

    /// <summary>
    /// The case a status check cannot see. The server is up, the credential is fine, and the run
    /// will still fail on the first call because the model is not there.
    /// </summary>
    [Fact]
    public async Task A_provider_that_answers_without_the_model_is_not_ready()
    {
        var status = await Check(new Canned(Ollama), "llama3:70b");

        Assert.Equal(ProviderHealth.ModelMissing, status.Health);
        Assert.Contains("llama3:70b", status.Summary);

        // Not reported as unreachable: nothing is broken, and the fix is a different one.
        Assert.NotEqual(ProviderHealth.Unreachable, status.Health);
    }

    /// <summary>
    /// Ollama installs "qwen2.5-coder:14b" and answers to "qwen2.5-coder" by resolving the tag.
    /// Calling that missing would put a warning on the commonest local setup there is.
    /// </summary>
    [Fact]
    public async Task A_model_named_without_its_tag_still_counts()
        => Assert.Equal(ProviderHealth.Ready, (await Check(new Canned(Ollama), "qwen2.5-coder")).Health);

    /// <summary>And the explanation is the one the RUN would have given, from the same place.</summary>
    [Fact]
    public async Task An_unreachable_provider_explains_itself_the_same_way()
    {
        var status = await Check(new Refuses(), "gemma4:12b");

        Assert.Equal(ProviderHealth.Unreachable, status.Health);
        Assert.Contains("not running", status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(status.Models);
    }

    /// <summary>
    /// With no model chosen there is no claim to make about one, so it asks only whether the
    /// provider answers, and says that and not more.
    /// </summary>
    [Fact]
    public async Task With_no_model_configured_it_only_reports_that_it_answered()
    {
        var status = await Check(new Canned(Ollama), null);

        Assert.Equal(ProviderHealth.Ready, status.Health);
        Assert.DoesNotContain("is there", status.Summary);
        Assert.Equal(2, status.Models.Count);
    }

    /// <summary>
    /// The time is part of the answer. A light with nothing behind it says how things were at an
    /// unstated moment, and the reader supplies "now" for free.
    /// </summary>
    [Fact]
    public async Task A_result_carries_when_it_was_learned()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 9, 22, 15, 0, TimeSpan.Zero));

        var status = await ProviderProbe.CheckAsync(
            new HttpClient(new Canned(Ollama)), ProviderKind.OllamaNative, "ollama",
            "http://localhost:11434/v1", null, null, "gemma4:12b", clock);

        Assert.Equal(clock.GetUtcNow(), status.At);
        Assert.Equal(default, ProviderStatus.Unknown.At);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
