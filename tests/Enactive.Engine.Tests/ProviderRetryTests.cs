namespace Enactive.Engine.Tests;

using System.Net;
using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Providers;
using Xunit;

public sealed class ProviderRetryTests
{
    private static readonly ChatRequest Request = new("model", []);

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    [InlineData(ProviderKind.Anthropic)]
    public async Task Empty_broken_stream_recovers_on_the_next_request(ProviderKind kind)
    {
        using var handler = new EmptyThenSuccess(kind);
        using var http = new HttpClient(handler);
        var descriptor = new ProviderDescriptor("test", "test", kind, "https://example.test", null, []);
        IChatProvider inner = kind switch
        {
            ProviderKind.OpenAiCompatible => new OpenAiCompatibleProvider(http, descriptor),
            ProviderKind.OllamaNative => new OllamaNativeProvider(http, descriptor),
            _ => new AnthropicProvider(http, descriptor)
        };
        var provider = new ResilientChatProvider(inner, delay: (_, _) => Task.CompletedTask);
        var received = new List<ChatStreamEvent>();
        await foreach (var ev in provider.StreamChatAsync(Request, default)) received.Add(ev);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal("ok", Assert.Single(received.OfType<TextDelta>()).Text);
        Assert.Single(received.OfType<FinishDelta>());
    }

    private sealed class EmptyThenSuccess(ProviderKind kind) : HttpMessageHandler
    {
        public int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = ++Attempts == 1 ? "" : kind switch
            {
                ProviderKind.OllamaNative => """{"message":{"content":"ok"},"done":true}""" + "\n",
                ProviderKind.Anthropic => """
                    data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}

                    data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"ok"}}

                    data: {"type":"content_block_stop","index":0}

                    data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

                    data: {"type":"message_stop"}

                    """,
                _ => "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Theory]
    [InlineData("ended", false, 3)]
    [InlineData("ended", true, 1)]
    [InlineData("http-io", false, 3)]
    [InlineData("http-io", true, 1)]
    [InlineData("io", false, 3)]
    [InlineData("io", true, 1)]
    [InlineData("invalid", false, 1)]
    [InlineData("protocol", false, 1)]
    [InlineData("cancel", false, 1)]
    public async Task Early_disconnects_retry_but_partial_answers_and_invalid_data_do_not(string kind, bool emit, int attempts)
    {
        Exception error = kind switch
        {
            "ended" => new HttpRequestException(HttpRequestError.ResponseEnded, "disconnected"),
            "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, "disconnected"),
            "io" => new IOException("connection reset"),
            "invalid" => new InvalidDataException("invalid tool fragments"),
            "protocol" => new HttpIOException(HttpRequestError.InvalidResponse, "invalid response"),
            _ => new OperationCanceledException()
        };
        var inner = new Failing(0) { Error = error, Emit = emit };
        var provider = new ResilientChatProvider(inner, delay: (_, _) => Task.CompletedTask);
        var received = new List<ChatStreamEvent>();
        var thrown = await Record.ExceptionAsync(async () =>
        { await foreach (var item in provider.StreamChatAsync(Request, default)) received.Add(item); });
        Assert.Same(error, thrown);
        Assert.Equal(attempts, inner.Attempts);
        Assert.Equal(emit ? 1 : 0, received.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Budget_is_checked_before_backoff_and_after_it(bool stream)
    {
        var now = DateTimeOffset.UtcNow;
        var budget = new RunBudget(new ExecutionLimits(MaxDurationSeconds: 1), now, () => now);
        var request = Request with { RetryBudget = budget };
        var inner = new Failing(503);
        var waits = 0;
        var provider = new ResilientChatProvider(inner, delay: (_, _) =>
        { waits++; now = now.AddSeconds(2); return Task.CompletedTask; });
        async Task Call()
        {
            if (stream) { await foreach (var _ in provider.StreamChatAsync(request, default)) { } }
            else await provider.CompleteAsync(request, default);
        }
        var exhausted = await Assert.ThrowsAsync<RetryBudgetExceededException>(Call);
        Assert.Contains("second(s)", exhausted.Message);
        Assert.IsType<HttpRequestException>(exhausted.InnerException);
        Assert.Equal(1, inner.Attempts);
        Assert.Equal(1, waits);

        // The next failing request finds the budget already exhausted and does not even sleep.
        await Assert.ThrowsAsync<RetryBudgetExceededException>(Call);
        Assert.Equal(2, inner.Attempts);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task Tokens_spent_by_another_step_during_backoff_prevent_retry()
    {
        var budget = new RunBudget(new ExecutionLimits(MaxTokens: 10, MaxSteps: 1), DateTimeOffset.UtcNow);
        budget.StepStarted(); // Dispatch limit must not prevent retry within this step.
        var inner = new Failing(503);
        var waits = 0;
        var provider = new ResilientChatProvider(inner, delay: (_, _) =>
        { waits++; budget.TokensUsed(10, 0); return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<RetryBudgetExceededException>(() =>
            provider.CompleteAsync(Request with { RetryBudget = budget }, default));
        Assert.Contains("token(s)", error.Message);
        Assert.Equal(1, waits);
        Assert.Equal(1, inner.Attempts);
    }

    [Theory]
    [InlineData(401, 1)]
    [InlineData(400, 1)]
    [InlineData(429, 3)]
    [InlineData(503, 3)]
    public async Task Only_transient_errors_are_retried_and_attempts_are_bounded(int status, int attempts)
    {
        var inner = new Failing(status);
        var provider = new ResilientChatProvider(inner, delay: (_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(Request, default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(attempts, inner.Attempts);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task Streaming_retries_only_before_the_first_event(bool emit, int attempts)
    {
        var inner = new Failing(503) { Emit = emit };
        var provider = new ResilientChatProvider(inner, delay: (_, _) => Task.CompletedTask);
        var events = 0;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in provider.StreamChatAsync(Request, default)) events++;
        });
        Assert.Equal(attempts, inner.Attempts);
        Assert.Equal(emit ? 1 : 0, events);
    }

    [Fact]
    public async Task Cancellation_during_backoff_prevents_another_request()
    {
        using var cts = new CancellationTokenSource();
        var inner = new Failing(429);
        var provider = new ResilientChatProvider(inner, delay: (_, ct) =>
        { cts.Cancel(); return Task.FromCanceled(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request, cts.Token));
        Assert.Equal(1, inner.Attempts);
    }

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    [InlineData(ProviderKind.Anthropic)]
    public async Task Adapters_preserve_status_and_retry_after(ProviderKind kind)
    {
        using var http = new HttpClient(new Busy());
        var descriptor = new ProviderDescriptor("test", "test", kind, "https://example.test", "key", []);
        IChatProvider inner = kind switch
        {
            ProviderKind.OpenAiCompatible => new OpenAiCompatibleProvider(http, descriptor),
            ProviderKind.OllamaNative => new OllamaNativeProvider(http, descriptor),
            _ => new AnthropicProvider(http, descriptor)
        };
        var waits = new List<TimeSpan>();
        var provider = new ResilientChatProvider(inner, delay: (time, _) => { waits.Add(time); return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(Request, default));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) }, waits);
    }

    private sealed class Busy : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("busy") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        }
    }

    private sealed class Failing(int status) : IChatProvider
    {
        public int Attempts;
        public bool Emit;
        public Exception? Error;
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        { Attempts++; throw Error ?? new HttpRequestException("test", null, (HttpStatusCode)status); }
        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Attempts++;
            await Task.CompletedTask;
            if (Emit) yield return new TextDelta("partial");
            throw Error ?? new HttpRequestException("test", null, (HttpStatusCode)status);
        }
    }
}
