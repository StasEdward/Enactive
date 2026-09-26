namespace Enactive.Engine.Tests;

using System.Net;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;

public sealed class ProviderReview8Tests
{
    public static IEnumerable<object[]> StreamErrors()
    {
        foreach (var kind in new[] { ProviderKind.Anthropic, ProviderKind.OpenAiCompatible, ProviderKind.OllamaNative })
        foreach (var status in new[] { 429, 503, 529, 400, 401, 403 })
        foreach (var partial in new[] { false, true }) yield return [kind, status, partial];
    }

    [Theory]
    [MemberData(nameof(StreamErrors))]
    public async Task Stream_status_retries_only_transient_errors_before_any_event(ProviderKind kind, int status, bool partial)
    {
        var error = JsonSerializer.Serialize(new { type = "error", error = new { code = status, message = "fixture error" } });
        using var handler = new Reply(_ => new(HttpStatusCode.OK)
        { Content = new StringContent((partial ? Prefix(kind) : "") + Frame(kind, error)) });
        using var http = new HttpClient(handler);
        var provider = new ResilientChatProvider(Provider(http, kind), delay: (_, _) => Task.CompletedTask);
        var delivered = new List<ChatStreamEvent>();
        var failure = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in provider.StreamChatAsync(new("model", []), default)) delivered.Add(item);
        });
        Assert.Equal(status, (int)failure.StatusCode!);
        Assert.Equal(!partial && status is 429 or 503 or 529 ? 3 : 1, handler.Calls);
        Assert.Equal(partial ? 1 : 0, delivered.Count);
    }

    [Theory]
    [InlineData("overloaded_error", 529)]
    [InlineData("rate_limit_error", 429)]
    [InlineData("api_error", 500)]
    [InlineData("invalid_request_error", 400)]
    [InlineData("authentication_error", 401)]
    [InlineData("unknown_future_error", null)]
    public void Named_protocol_errors_keep_their_status_without_guessing_from_message(string type, int? status)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { error = new { type, message = "overloaded 529 retry" } }));
        Assert.Equal(status, (int?)StreamEnd.ErrorIn("fixture", json.RootElement)!.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_529_retries_for_completion_and_stream(bool streaming)
    {
        using var handler = new Reply(_ => new((HttpStatusCode)529) { Content = new StringContent("overloaded") });
        using var http = new HttpClient(handler);
        var provider = new ResilientChatProvider(Provider(http, ProviderKind.Anthropic), delay: (_, _) => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (streaming) await Drain(provider);
            else await provider.CompleteAsync(new("model", []), default);
        });
        Assert.Equal(529, (int)failure.StatusCode!);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("https://proxy.test", "/v1/messages")]
    [InlineData("https://proxy.test/v1", "/v1/messages")]
    [InlineData("https://proxy.test/v1/", "/v1/messages")]
    [InlineData("https://proxy.test/prefix/v1/", "/prefix/v1/messages")]
    [InlineData("https://proxy.test/prefix", "/prefix/v1/messages")]
    public async Task Anthropic_uses_exactly_one_version_segment(string url, string expected)
    {
        using var handler = new Reply(request =>
        {
            Assert.Equal(expected, request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture refusal") };
        });
        using var http = new HttpClient(handler);
        var provider = Provider(http, ProviderKind.Anthropic, url);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(new("model", []), default));
        await Assert.ThrowsAsync<HttpRequestException>(() => Drain(provider));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Bad_endpoint_is_a_configuration_error_not_invalid_json(ProviderKind kind)
    {
        using var handler = new Reply(_ => throw new InvalidOperationException("No HTTP request expected"));
        using var http = new HttpClient(handler);
        foreach (var url in new[] { "", "/relative?token=secret", "http://[broken", "file:///tmp/secret" })
        {
            var provider = Provider(http, kind, url);
            var streamError = await Assert.ThrowsAsync<ArgumentException>(() => Drain(provider));
            var completionError = await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteAsync(new("model", []), default));
            foreach (var error in new[] { streamError, completionError })
            {
                Assert.Contains("endpoint URL", error.Message);
                Assert.DoesNotContain("secret", error.Message);
                Assert.DoesNotContain("JSON", error.Message);
            }
        }
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(401)]
    public async Task Timed_out_error_body_preserves_status_and_retry_after(int status)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StreamContent(new TimedOutBody()) };
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ProviderHttpError.ReadBodyAsync(response, 1, default));
        Assert.Equal(status, (int)error.StatusCode!);
        Assert.Equal(TimeSpan.FromSeconds(7), error.Data[ProviderHttpError.RetryAfterKey]);
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task Cancellation_while_reading_error_body_remains_cancellation()
    {
        using var body = new WaitingBody();
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StreamContent(body) };
        using var cancellation = new CancellationTokenSource();
        var reading = ProviderHttpError.ReadBodyAsync(response, 60, cancellation.Token);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    private static IChatProvider Provider(HttpClient http, ProviderKind kind, string url = "https://fixture.test")
    {
        var descriptor = new ProviderDescriptor("fixture", "fixture", kind, url, null, []);
        return kind switch
        {
            ProviderKind.Anthropic => new AnthropicProvider(http, descriptor),
            ProviderKind.OllamaNative => new OllamaNativeProvider(http, descriptor),
            _ => new OpenAiCompatibleProvider(http, descriptor)
        };
    }
    private static async Task Drain(IChatProvider provider)
    { await foreach (var _ in provider.StreamChatAsync(new("model", []), default)) { } }
    private static string Frame(ProviderKind kind, string body)
        => kind == ProviderKind.OllamaNative ? body + "\n" : "data: " + body + "\n\n";
    private static string Prefix(ProviderKind kind) => Frame(kind, kind switch
    {
        ProviderKind.Anthropic => """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"partial"}}""",
        ProviderKind.OllamaNative => """{"message":{"content":"partial"}}""",
        _ => """{"choices":[{"delta":{"content":"partial"}}]}"""
    });
    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(reply(request)); }
    }
    private sealed class TimedOutBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new TimeoutException("fixture idle"));
    }
    private sealed class WaitingBody : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
