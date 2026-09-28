namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class AnthropicTemperatureTests
{
    private const string Deprecated = """{"error":{"type":"invalid_request_error","message":"\u0060temperature\u0060 is deprecated for this model."}}""";
    private const string Ok = """{"content":[{"type":"text","text":"{}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";
    private static ProviderDescriptor Descriptor(string url = "https://test.invalid") => new("a", "a", ProviderKind.Anthropic, url, null, []);
    private static ChatRequest Request(string model = "m") => new(model, [ChatMessage.User("Plan a synthetic task")], Temperature: 0);

    [Fact]
    public async Task Confirmed_temperature_rejection_is_remembered_only_for_this_model_and_instance()
    {
        using var handler = new Reply(Deprecated, Ok, Ok, Ok, Ok);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, Descriptor());
        await provider.CompleteAsync(Request(), default);
        await provider.CompleteAsync(Request(), default);
        await provider.CompleteAsync(Request("other"), default);
        await new AnthropicProvider(http, Descriptor("https://other.invalid")).CompleteAsync(Request(), default);
        Assert.Equal(new[] { true, false, false, true, true }, handler.Temperatures);
    }

    [Fact]
    public async Task Failed_retry_does_not_learn_a_temperature_capability()
    {
        using var handler = new Reply(Deprecated, """{"error":{"type":"invalid_request_error","message":"Invalid messages"}}""", Ok);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, Descriptor());
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(Request(), default));
        await provider.CompleteAsync(Request(), default);
        Assert.Equal(new[] { true, false, true }, handler.Temperatures);
    }

    [Theory]
    [InlineData("""{"error":{"type":"invalid_request_error","message":"Context too long; temperature option is deprecated elsewhere"}}""")]
    [InlineData("<html>temperature deprecated</html>")]
    public async Task Unrelated_error_does_not_trigger_temperature_retry(string error)
    {
        using var handler = new Reply(error);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new AnthropicProvider(http, Descriptor()).CompleteAsync(Request(), default));
        Assert.Single(handler.Temperatures);
    }

    private sealed class Reply(params string[] bodies) : HttpMessageHandler
    {
        public List<bool> Temperatures { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = bodies[Temperatures.Count];
            Temperatures.Add(json.RootElement.TryGetProperty("temperature", out _));
            return new(body == Ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest) {
                Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
