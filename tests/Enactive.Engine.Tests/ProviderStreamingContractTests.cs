namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class ProviderStreamingContractTests
{
    [Fact]
    public async Task Anthropic_emits_text_before_the_body_finishes_and_times_out_only_on_idle()
    {
        var prefix = """
            data: {"type":"message_start","message":{"usage":{"input_tokens":5}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

            """ + "\n";
        using var http = new HttpClient(new StreamReply(new StalledStream(Encoding.UTF8.GetBytes(prefix))))
            { Timeout = TimeSpan.FromMilliseconds(100) };
        var descriptor = new ProviderDescriptor("test", "test", ProviderKind.Anthropic, "https://test.invalid", null, [], StreamIdleTimeoutSeconds: 1);
        var provider = new AnthropicProvider(http, descriptor);
        await using var events = provider.StreamChatAsync(new("model", []), default).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("hello", Assert.IsType<TextDelta>(events.Current).Text);
        await Assert.ThrowsAsync<TimeoutException>(() => events.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task Reasoning_profile_is_explicit_and_serializes_the_effective_limit()
    {
        using var handler = new Capture();
        using var http = new HttpClient(handler);
        var descriptor = new ProviderDescriptor("test", "test", ProviderKind.OpenAiCompatible,
            "https://test.invalid", null, [], OpenAiReasoningProfile: true);
        await new OpenAiCompatibleProvider(http, descriptor).CompleteAsync(
            new("model", [ChatMessage.System("instructions"), ChatMessage.User("hello")], Temperature: 0, MaxTokens: 17), default);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(17, sent.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(sent.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(sent.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal("developer", sent.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Anthropic_deltas_keep_reasoning_tool_arguments_and_usage_separate(bool complete)
    {
        var body = """
            data: {"type":"message_start","message":{"usage":{"input_tokens":5,"cache_read_input_tokens":10,"cache_creation_input_tokens":3}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"reason"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call","name":"read_file","input":{}}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"file\"}"}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}

            """ + (complete ? "\ndata: {\"type\":\"message_stop\"}\n\n" : "\n");
        using var http = new HttpClient(new StreamReply(new MemoryStream(Encoding.UTF8.GetBytes(body))));
        var provider = new AnthropicProvider(http, new("test", "test", ProviderKind.Anthropic, "https://test.invalid", null, []));
        var events = new List<ChatStreamEvent>();
        async Task Read() { await foreach (var ev in provider.StreamChatAsync(new("model", []), default)) events.Add(ev); }
        if (!complete) { await Assert.ThrowsAsync<HttpRequestException>(Read); return; }
        await Read();
        Assert.Equal("reason", Assert.Single(events.OfType<ReasoningDelta>()).Text);
        Assert.Equal("{\"path\":\"file\"}", string.Concat(events.OfType<ToolCallDelta>().Select(c => c.ArgumentsJson)));
        Assert.Equal(new UsageDelta(18, 7, 10) { CacheCreationPromptTokens = 3 }, Assert.Single(events.OfType<UsageDelta>()));
        Assert.Equal("tool_use", Assert.Single(events.OfType<FinishDelta>()).Reason);
    }

    private sealed class StreamReply(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""") };
        }
    }

    private sealed class StalledStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, ct);
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }
}
