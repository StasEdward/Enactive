namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Providers;
using Xunit;

/// <summary>
/// Prompt caching on the Anthropic adapter — FIX_PLAN §9am.
///
/// <para>The console showed ZERO cached tokens for the account, which was exactly right rather than
/// a reporting gap: nothing sent <c>cache_control</c> anywhere. The worker loop is the case caching
/// exists for — every tool call re-sends the tool definitions, the system prompt and a transcript
/// that only grows, so a twelve-step run paid for the same prefix a few dozen times.</para>
///
/// <para>The half of this that must never ship alone is the ACCOUNTING. With a breakpoint in the
/// request, <c>input_tokens</c> counts only what follows it; a reader of that one field would report
/// a fraction of what was spent, and the run would look cheaper precisely because the sum had
/// broken. Both halves are here, and both were shown red with their own fix reverted.</para>
/// </summary>
public sealed class PromptCachingTests
{
    /// <summary>Captures the request body so the wire can be asserted on, and answers with a
    /// minimal completion so the call returns.</summary>
    private sealed class Capturing : HttpMessageHandler
    {
        private readonly string _body;

        public Capturing(string body) => _body = body;

        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private const string Answer = """
        {
          "id": "msg_1", "type": "message", "role": "assistant",
          "content": [{"type": "text", "text": "done"}],
          "stop_reason": "end_turn",
          "usage": {"input_tokens": 7, "output_tokens": 3}
        }
        """;

    private static ChatRequest Request(bool withTools = true, bool withSystem = true)
    {
        var messages = new List<ChatMessage>();
        if (withSystem)
            messages.Add(new ChatMessage(ChatRole.System, "You are a developer agent."));
        messages.Add(new ChatMessage(ChatRole.User, "Read the file."));

        return new ChatRequest(
            "claude-sonnet-4-6",
            messages,
            withTools
                ? new[]
                {
                    new ToolDefinition("read_file", "Read a file", """{"type":"object"}"""),
                    new ToolDefinition("write_file", "Write a file", """{"type":"object"}"""),
                }
                : null);
    }

    private static async Task<JsonElement> SentFor(ChatRequest request, string answer = Answer)
    {
        var handler = new Capturing(answer);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(
            http, new ProviderDescriptor("anthropic", "Anthropic", ProviderKind.Anthropic,
                                         "https://api.anthropic.com", "key", new[] { "claude-sonnet-4-6" }));

        await provider.CompleteAsync(request, CancellationToken.None);

        return JsonDocument.Parse(handler.Sent!).RootElement.Clone();
    }

    private static bool IsCached(JsonElement block)
        => block.TryGetProperty("cache_control", out var cc)
        && cc.TryGetProperty("type", out var type)
        && type.GetString() == "ephemeral";

    // ── where the breakpoints go ────────────────────────────────────────────

    /// <summary>
    /// The system prompt goes as an ARRAY of blocks, not a string. A string cannot carry
    /// <c>cache_control</c>, and that alone is why this could not simply be switched on.
    /// </summary>
    [Fact]
    public async Task The_system_prompt_is_a_cached_block_and_not_a_string()
    {
        var sent = await SentFor(Request());

        var system = sent.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);

        var block = system[0];
        Assert.Equal("You are a developer agent.", block.GetProperty("text").GetString());
        Assert.True(IsCached(block));
    }

    /// <summary>
    /// On the LAST tool, because a breakpoint caches everything before it and the tools are one
    /// prefix. The tool set does not change within a run, which makes this the cheapest and most
    /// certain of the three.
    /// </summary>
    [Fact]
    public async Task The_last_tool_definition_carries_the_breakpoint()
    {
        var sent = await SentFor(Request());

        var tools = sent.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.False(IsCached(tools[0]));
        Assert.True(IsCached(tools[1]));
    }

    /// <summary>The end of the transcript, which is where the growth is and therefore where the
    /// money is.</summary>
    [Fact]
    public async Task The_last_message_carries_the_breakpoint()
    {
        var sent = await SentFor(Request());

        var messages = sent.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        var content = last.GetProperty("content");

        Assert.True(IsCached(content[content.GetArrayLength() - 1]));
    }

    /// <summary>
    /// Four are allowed and three are used. Going over is a 400 from the API, so the count is worth
    /// pinning rather than trusting to the next person who adds one.
    /// </summary>
    [Fact]
    public async Task No_more_than_four_breakpoints_are_ever_sent()
    {
        var sent = await SentFor(Request());

        Assert.InRange(CountBreakpoints(sent), 1, 4);
    }

    /// <summary>A request with no tools still caches what it has, and sends no empty tools array to
    /// be refused for.</summary>
    [Fact]
    public async Task A_request_without_tools_still_caches_the_system_prompt()
    {
        var sent = await SentFor(Request(withTools: false));

        Assert.False(sent.TryGetProperty("tools", out _));
        Assert.True(IsCached(sent.GetProperty("system")[0]));
    }

    /// <summary>And one with no system prompt sends none — an empty block would be a cache write
    /// for nothing.</summary>
    [Fact]
    public async Task A_request_without_a_system_prompt_sends_none()
    {
        var sent = await SentFor(Request(withSystem: false));

        Assert.False(sent.TryGetProperty("system", out _));
    }

    private static int CountBreakpoints(JsonElement sent)
    {
        var count = 0;

        if (sent.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.Array)
            foreach (var block in system.EnumerateArray())
                if (IsCached(block)) count++;

        if (sent.TryGetProperty("tools", out var tools))
            foreach (var tool in tools.EnumerateArray())
                if (IsCached(tool)) count++;

        foreach (var message in sent.GetProperty("messages").EnumerateArray())
            foreach (var block in message.GetProperty("content").EnumerateArray())
                if (IsCached(block)) count++;

        return count;
    }

    // ── the accounting, which must not ship a commit later ──────────────────

    /// <summary>
    /// The three fields are summed. <c>input_tokens</c> alone is what follows the last breakpoint —
    /// 50 here against a 200,000-token prefix read from the cache — so a reader of it would report
    /// 50 and the run would look four thousand times cheaper than it was.
    /// </summary>
    [Fact]
    public async Task Cached_and_created_tokens_are_counted_as_input()
    {
        var completion = await CompleteWith("""
            {
              "id": "m", "type": "message", "role": "assistant",
              "content": [{"type": "text", "text": "ok"}],
              "stop_reason": "end_turn",
              "usage": {
                "input_tokens": 50,
                "cache_read_input_tokens": 200000,
                "cache_creation_input_tokens": 500,
                "output_tokens": 12
              }
            }
            """);

        Assert.Equal(200550, completion.PromptTokens);
        Assert.Equal(12, completion.CompletionTokens);
    }

    /// <summary>The cached share is reported separately, because it is the only way to see the
    /// feature is working: every other number looks the same whether it is on or off.</summary>
    [Fact]
    public async Task The_cached_share_is_reported()
    {
        var completion = await CompleteWith("""
            {
              "id": "m", "type": "message", "role": "assistant",
              "content": [{"type": "text", "text": "ok"}],
              "stop_reason": "end_turn",
              "usage": {"input_tokens": 50, "cache_read_input_tokens": 900, "output_tokens": 2}
            }
            """);

        Assert.Equal(900, completion.CachedPromptTokens);
        Assert.Equal(950, completion.PromptTokens);
    }

    /// <summary>
    /// A response from before caching, or a miss, reads exactly as it did: the sum of one number is
    /// that number. A cache field that is absent must not become a zero that changes a total.
    /// </summary>
    [Fact]
    public async Task A_response_with_no_cache_fields_reads_as_it_always_did()
    {
        var completion = await CompleteWith(Answer);

        Assert.Equal(7, completion.PromptTokens);
        Assert.Equal(3, completion.CompletionTokens);
        Assert.Null(completion.CachedPromptTokens);
    }

    /// <summary>
    /// Null, not zero, when the response counts nothing at all. "This provider does not tell us" and
    /// "this turn cost nothing" are different facts and are shown differently.
    /// </summary>
    [Fact]
    public async Task A_response_with_no_usage_at_all_reports_nothing()
    {
        var completion = await CompleteWith("""
            {"id":"m","type":"message","role":"assistant",
             "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}
            """);

        Assert.Null(completion.PromptTokens);
        Assert.Null(completion.CachedPromptTokens);
    }

    private static async Task<ChatCompletion> CompleteWith(string answer)
    {
        using var http = new HttpClient(new Capturing(answer));
        var provider = new AnthropicProvider(
            http, new ProviderDescriptor("anthropic", "Anthropic", ProviderKind.Anthropic,
                                         "https://api.anthropic.com", "key", new[] { "claude-sonnet-4-6" }));

        return await provider.CompleteAsync(Request(), CancellationToken.None);
    }
}
