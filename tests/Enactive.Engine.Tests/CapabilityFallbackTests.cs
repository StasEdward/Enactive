namespace Enactive.Engine.Tests;

using System.Net;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class CapabilityFallbackTests
{
    [Theory]
    [InlineData(false, "context_length_exceeded")]
    [InlineData(true, "context_length_exceeded")]
    [InlineData(false, "invalid messages")]
    [InlineData(true, "invalid messages")]
    [InlineData(false, "invalid schema in response_format: unsupported property type")]
    [InlineData(true, "unknown field temperature")]
    [InlineData(false, "response_format validation failed: model is unavailable now")]
    public async Task Unrelated_errors_do_not_retry_or_disable(bool stream, string error)
    {
        using var handler = new Recording(stream, error, 1);
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(http, Descriptor());
        await Assert.ThrowsAsync<HttpRequestException>(() => Call(provider, stream));
        Assert.Single(handler.Present);
        await Call(provider, stream);
        Assert.Equal(new[] { true, true }, handler.Present);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Only_successful_fallback_is_cached(bool stream, bool secondFailure)
    {
        using var handler = new Recording(stream, "unsupported parameter: " + Field(stream), secondFailure ? 2 : 1);
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(http, Descriptor());
        if (secondFailure) await Assert.ThrowsAsync<HttpRequestException>(() => Call(provider, stream));
        else await Call(provider, stream);
        await Call(provider, stream);
        Assert.Equal(new[] { true, false, secondFailure }, handler.Present);
    }

    [Theory]
    [InlineData(false, "endpoint")]
    [InlineData(true, "endpoint")]
    [InlineData(false, "key")]
    [InlineData(true, "headers")]
    [InlineData(false, "model")]
    public async Task Changed_configuration_probes_again(bool stream, string change)
    {
        using var handler = new Recording(stream, Field(stream) + " is not supported", 1);
        using var http = new HttpClient(handler);
        var descriptor = Descriptor();
        await Call(new OpenAiCompatibleProvider(http, descriptor), stream);
        await Call(new OpenAiCompatibleProvider(http, descriptor), stream);
        var changed = change switch
        {
            "endpoint" => descriptor with { BaseUrl = "https://different.test/v1" },
            "key" => descriptor with { ApiKey = "new-key" },
            "headers" => descriptor with { Headers = new Dictionary<string, string> { ["X-Version"] = "2" } },
            _ => descriptor
        };
        await Call(new OpenAiCompatibleProvider(http, changed), stream, change == "model" ? "other" : "model");
        Assert.Equal(new[] { true, false, false, true }, handler.Present);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deepseek_unavailable_response_format_retries_and_caches_only_success(bool secondFailure)
    {
        using var handler = new Recording(false,
            "This response_format type is unavailable now (request_id: c169c721-0fff-4c91-b732-a04ccd02486d)",
            secondFailure ? 2 : 1);
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(http, Descriptor());
        if (secondFailure) await Assert.ThrowsAsync<HttpRequestException>(() => Call(provider, false));
        else await Call(provider, false);
        await Call(provider, false);
        Assert.Equal(new[] { true, false, secondFailure }, handler.Present);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_default_http_headers_reprobes_on_same_adapter(bool stream)
    {
        using var handler = new Recording(stream, Field(stream) + " is not supported", 1);
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(http, Descriptor());
        await Call(provider, stream);
        await Call(provider, stream);
        http.DefaultRequestHeaders.Add("X-Backend", "new-server");
        await Call(provider, stream);
        Assert.Equal(new[] { true, false, false, true }, handler.Present);
    }

    private static string Field(bool stream) => stream ? "stream_options" : "response_format";
    private static ProviderDescriptor Descriptor() => new(Guid.NewGuid().ToString(), "test",
        ProviderKind.OpenAiCompatible, "https://example.test/v1", "key", new[] { "model" });
    private static async Task Call(OpenAiCompatibleProvider provider, bool stream, string model = "model")
    {
        var request = new ChatRequest(model, new[] { ChatMessage.User("hi") }, ResponseSchema: "{}");
        if (stream) { await foreach (var item in provider.StreamChatAsync(request, default)) { } }
        else await provider.CompleteAsync(request, default);
    }
    private sealed class Recording(bool stream, string error, int failures) : HttpMessageHandler
    {
        public List<bool> Present { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Present.Add(body.RootElement.TryGetProperty(Field(stream), out _));
            var fail = Present.Count <= failures;
            return new HttpResponseMessage(fail ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(fail ? JsonSerializer.Serialize(new { error = new {
                    message = error, type = "invalid_request_error", param = (string?)null, code = "invalid_request_error" } })
                    : stream ? "data: [DONE]\n\n" : """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""")
            };
        }
    }
}
