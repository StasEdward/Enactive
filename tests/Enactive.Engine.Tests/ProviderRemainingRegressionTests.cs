namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class ProviderRemainingRegressionTests
{
    private static IChatProvider Build(HttpClient http, ProviderDescriptor d) => d.Kind switch
    {
        ProviderKind.Anthropic => new AnthropicProvider(http, d),
        ProviderKind.OllamaNative => new OllamaNativeProvider(http, d),
        _ => new OpenAiCompatibleProvider(http, d)
    };
    private static ProviderDescriptor Descriptor(ProviderKind kind) => new("test", "test", kind, "https://proxy.invalid", "default-key", [], StreamIdleTimeoutSeconds: 1);
    private static string Completion(ProviderKind kind) => kind switch
    {
        ProviderKind.Anthropic => """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""",
        ProviderKind.OllamaNative => """{"message":{"content":"ok"},"done":true}""",
        _ => """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""
    };
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Custom_auth_replaces_default_and_content_headers_reach_content(ProviderKind kind)
    {
        var key = kind == ProviderKind.Anthropic ? "x-api-key" : "Authorization";
        using var handler = new Capture(request =>
        {
            Assert.Equal("custom-key", Assert.Single(request.Headers.GetValues(key)));
            Assert.Equal("application/custom+json", request.Content!.Headers.ContentType!.MediaType);
            return Reply(Completion(kind));
        });
        using var http = new HttpClient(handler);
        await Build(http, Descriptor(kind) with { Headers = new Dictionary<string, string> { [key] = "custom-key", ["Content-Type"] = "application/custom+json" } })
            .CompleteAsync(new("model", [ChatMessage.User("hi")]), default);
    }

    [Fact]
    public async Task Anthropic_omits_empty_text_turns()
    {
        using var handler = new Capture(_ => Reply(Completion(ProviderKind.Anthropic)));
        using var http = new HttpClient(handler);
        await Build(http, Descriptor(ProviderKind.Anthropic)).CompleteAsync(new("model",
            [ChatMessage.User("hi"), ChatMessage.Assistant(""), ChatMessage.User("next")]), default);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData("max_tokens plus prompt tokens > 1200 exceeds the context window")]
    [InlineData("invalid max_tokens argument")]
    public async Task Context_or_unknown_errors_do_not_learn_a_model_output_cap(string message)
    {
        using var handler = new Capture(_ => Reply(JsonSerializer.Serialize(new { error = new { message } }), HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler);
        var provider = Build(http, Descriptor(ProviderKind.Anthropic));
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(new("model", [], MaxTokens: 32000), default));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, json =>
        {
            using var body = JsonDocument.Parse(json);
            Assert.Equal(32000, body.RootElement.GetProperty("max_tokens").GetInt32());
        });
    }

    [Theory]
    [InlineData("reasoning_content", false)]
    [InlineData("reasoning", false)]
    [InlineData("reasoning_content", true)]
    [InlineData("reasoning", true)]
    public async Task Openai_reasoning_stays_separate_from_answer(string field, bool streaming)
    {
        var message = new Dictionary<string, object?> { [field] = "reason", ["content"] = "answer" };
        var choice = new Dictionary<string, object?> { [streaming ? "delta" : "message"] = message, ["finish_reason"] = "stop" };
        var json = JsonSerializer.Serialize(new { choices = new[] { choice } });
        using var handler = new Capture(_ => Reply(streaming ? "data: " + json + "\n\ndata: [DONE]\n\n" : json));
        using var http = new HttpClient(handler);
        var provider = Build(http, Descriptor(ProviderKind.OpenAiCompatible));
        if (streaming)
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var item in provider.StreamChatAsync(new("model", []), default)) events.Add(item);
            Assert.Equal("reason", Assert.Single(events.OfType<ReasoningDelta>()).Text);
            Assert.Equal("answer", Assert.Single(events.OfType<TextDelta>()).Text);
        }
        else
        {
            var reply = await provider.CompleteAsync(new("model", []), default);
            Assert.Equal("reason", reply.Thinking);
            Assert.Equal("answer", reply.Message.Content);
        }
    }

    [Fact]
    public async Task Ollama_declared_context_window_is_also_sent_to_the_server()
    {
        using var handler = new Capture(_ => Reply(Completion(ProviderKind.OllamaNative)));
        using var http = new HttpClient(handler);
        var provider = Build(http, Descriptor(ProviderKind.OllamaNative) with { ContextWindowTokens = 8192 });
        var request = new ChatRequest("model", []);
        Assert.Equal(8192, provider.ContextWindow(request));
        Assert.Equal(4096, provider.ContextWindow(request with { NumCtx = 4096 }));
        await provider.CompleteAsync(request, default);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(8192, body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Anthropic_catalog_uses_configured_proxy_and_headers()
    {
        using var handler = new Capture(request =>
        {
            Assert.Equal("https://proxy.invalid/gateway/v1/models?limit=1000", request.RequestUri!.AbsoluteUri);
            Assert.Equal("override", Assert.Single(request.Headers.GetValues("x-api-key")));
            return Reply("""{"data":[{"id":"available"}]}""");
        });
        using var http = new HttpClient(handler);
        Assert.Equal(new[] { "available" }, await ModelFetch.ForAsync(http, ProviderKind.Anthropic,
            "https://proxy.invalid/gateway/v1", "key", new Dictionary<string, string> { ["x-api-key"] = "override" }));
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Error_body_stall_has_an_idle_timeout_and_releases_the_response(ProviderKind kind)
    {
        var stalled = new Stalled();
        using var handler = new Capture(_ => new(HttpStatusCode.BadRequest) { Content = new StreamContent(stalled) });
        using var http = new HttpClient(handler);
        var provider = Build(http, Descriptor(kind));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(new("model", []), default)) { }
        });
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.True(stalled.Disposed);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Cancelling_error_body_read_is_cancellation_and_releases_response(ProviderKind kind)
    {
        var stalled = new Stalled();
        using var handler = new Capture(_ => new(HttpStatusCode.BadRequest) { Content = new StreamContent(stalled) });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var provider = Build(http, Descriptor(kind) with { StreamIdleTimeoutSeconds = 60 });
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(new("model", []), cancellation.Token)) { }
        });
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.True(stalled.Disposed);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Oversized_error_body_is_truncated_with_status_preserved(ProviderKind kind)
    {
        var source = new Counted(Encoding.UTF8.GetBytes(new string('x', 100000)));
        using var handler = new Capture(_ => new(HttpStatusCode.BadRequest) { Content = new StreamContent(source) });
        using var http = new HttpClient(handler);
        var provider = Build(http, Descriptor(kind));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(new("model", []), default)) { }
        });
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.InRange(source.BytesRead, 65536, 69632);
        Assert.True(error.Message.Length < 1000);
    }

    private sealed class Counted(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            BytesRead += read;
            return read;
        }
    }

    private sealed class Stalled : MemoryStream
    {
        public bool Disposed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return reply(request);
        }
    }
}
