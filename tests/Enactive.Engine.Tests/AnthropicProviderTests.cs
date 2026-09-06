namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// The Anthropic adapter against a canned Messages API response — the real parsing and the real
/// delta emission, with only the socket replaced.
///
/// Review finding #7: every tool_use block was emitted as <c>ToolCallDelta(0, …)</c>. The
/// orchestrator merges deltas by index, so two calls in one completion became one call carrying the
/// last name and id with the first call's arguments, and the second action was simply lost.
/// </summary>
public sealed class AnthropicProviderTests
{
    private const string TwoToolUseBlocks = """
        {
          "id": "msg_1",
          "type": "message",
          "role": "assistant",
          "content": [
            {"type": "text", "text": "I will read it and then write the copy."},
            {"type": "tool_use", "id": "toolu_read", "name": "read_file",
             "input": {"path": "source.txt"}},
            {"type": "tool_use", "id": "toolu_write", "name": "write_file",
             "input": {"path": "copy.txt", "content": "the copy"}}
          ],
          "stop_reason": "tool_use",
          "usage": {"input_tokens": 11, "output_tokens": 22}
        }
        """;

    [Fact]
    public async Task Each_tool_use_block_gets_its_own_index()
    {
        var deltas = await StreamAsync(TwoToolUseBlocks);
        var calls = deltas.OfType<ToolCallDelta>().ToArray();

        Assert.Equal(2, calls.Length);
        Assert.Equal(new[] { 0, 1 }, calls.Select(c => c.Index).ToArray());
    }

    [Fact]
    public async Task Each_call_keeps_its_own_name_id_and_arguments()
    {
        var deltas = await StreamAsync(TwoToolUseBlocks);
        var calls = deltas.OfType<ToolCallDelta>().ToArray();

        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("toolu_read", calls[0].Id);
        Assert.Contains("source.txt", calls[0].ArgumentsJson ?? "", StringComparison.Ordinal);

        Assert.Equal("write_file", calls[1].Name);
        Assert.Equal("toolu_write", calls[1].Id);
        Assert.Contains("copy.txt", calls[1].ArgumentsJson ?? "", StringComparison.Ordinal);
        // The write must NOT have inherited the read's arguments — that is the destructive half of
        // the old merge, because write_file with no content used to truncate the file.
        Assert.Contains("the copy", calls[1].ArgumentsJson ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_and_usage_still_come_through()
    {
        var deltas = await StreamAsync(TwoToolUseBlocks);

        Assert.Contains(deltas.OfType<TextDelta>(), t => t.Text.Contains("read it", StringComparison.Ordinal));
        var usage = Assert.Single(deltas.OfType<UsageDelta>());
        Assert.Equal(11, usage.PromptTokens);
        Assert.Equal(22, usage.CompletionTokens);
    }

    private static async Task<List<ChatStreamEvent>> StreamAsync(string responseJson)
    {
        using var http = new HttpClient(new CannedHandler(responseJson))
        {
            BaseAddress = new Uri("https://api.anthropic.test")
        };

        var provider = new AnthropicProvider(
            http,
            new ProviderDescriptor("anthropic", "Anthropic", ProviderKind.Anthropic,
                "https://api.anthropic.test", "test-key", new[] { "claude-test" }));

        var request = new ChatRequest("claude-test", new[] { ChatMessage.User("do the thing") });

        var events = new List<ChatStreamEvent>();
        await foreach (var ev in provider.StreamChatAsync(request, CancellationToken.None))
            events.Add(ev);
        return events;
    }

    /// <summary>Answers every request with the same body. No network, no retries to wait for.</summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly string _body;

        public CannedHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
