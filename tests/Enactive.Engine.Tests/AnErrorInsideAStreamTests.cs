namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// An error a provider reports INSIDE a stream that began with HTTP 200, and a stream that stops
/// before its protocol says it finished, are failures - and nothing streamed before them is acted on.
///
/// <para><b>Found 2026-09-24</b>. Neither
/// parser knew a top-level <c>error</c>, so it produced no event, and nothing required a stream to
/// finish. Through the real orchestrator and the Ollama adapter: a <c>write_file</c> call, then
/// <c>{"error": ...}</c>, then the end of the stream - the file was WRITTEN, the task Completed, and no
/// ErrorObserved recorded. The tests of provider failures all started from an exception the fake
/// provider threw, and the fake always finishes its streams.</para>
/// </summary>
public sealed class AnErrorInsideAStreamTests
{
    /// <summary>Answers each request with the next body, as HTTP 200 - the error is IN the body.</summary>
    private sealed class Scripted(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        public string Last { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = _bodies.Count > 1 ? _bodies.Dequeue() : _bodies.Peek();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Last, Encoding.UTF8, "application/json")
            });
        }
    }

    private static ProviderDescriptor Descriptor(string id, ProviderKind kind)
        => new(id, id, kind, "https://" + id + ".test", "key", new[] { "m" });

    private static async Task<List<ChatStreamEvent>> Drain(IChatProvider provider)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.StreamChatAsync(new ChatRequest("m", new[] { ChatMessage.User("go") }), CancellationToken.None))
            events.Add(e);
        return events;
    }

    // ── Ollama: one JSON object per line ─────────────────────────────────────

    private const string OllamaText = """{"message":{"role":"assistant","content":"Hel"},"done":false}""";
    private const string OllamaTool = """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"write_file","arguments":{"path":"x.md","content":"hi"}}}]},"done":false}""";
    private const string OllamaError = """{"error":"generation failed"}""";
    private const string OllamaDone = """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""";

    public static TheoryData<string, string> OllamaBroken => new()
    {
        { "error before any text", OllamaError },
        { "error after text", OllamaText + "\n" + OllamaError },
        { "error after a tool call", OllamaTool + "\n" + OllamaError },
        { "stopped before done", OllamaText },
    };

    [Theory]
    [MemberData(nameof(OllamaBroken))]
    public async Task Ollama_a_stream_that_errs_or_stops_short_fails(string _, string body)
    {
        using var http = new HttpClient(new Scripted(body));
        var provider = new OllamaNativeProvider(http, Descriptor("ollama-a", ProviderKind.OllamaNative));

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => Drain(provider));
        Assert.Contains("ollama-a", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. A finished stream with a tool call is an answer, whole.</summary>
    [Fact]
    public async Task Ollama_a_finished_stream_is_an_answer()
    {
        using var http = new HttpClient(new Scripted(OllamaTool + "\n" + OllamaDone));
        var provider = new OllamaNativeProvider(http, Descriptor("ollama-a", ProviderKind.OllamaNative));

        var events = await Drain(provider);

        Assert.Contains(events, e => e is ToolCallDelta { Name: "write_file" });
        Assert.Contains(events, e => e is FinishDelta);
    }

    // ── OpenAI-compatible: server-sent events ────────────────────────────────

    private const string SseText = "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n";
    private const string SseTool = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"write_file\",\"arguments\":\"{\\\"path\\\":\\\"x.md\\\",\\\"content\\\":\\\"hi\\\"}\"}}]}}]}\n\n";
    private const string SseError = "data: {\"error\":{\"message\":\"generation failed\"}}\n\n";
    private const string SseFinish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n";
    private const string SseDone = "data: [DONE]\n\n";

    public static TheoryData<string, string> OpenAiBroken => new()
    {
        { "error before any text", SseError },
        { "error after text", SseText + SseError },
        { "error after a tool call", SseTool + SseError },
        { "error after a tool call, then [DONE]", SseTool + SseError + SseDone },
        { "stopped with neither finish_reason nor [DONE]", SseText },
    };

    [Theory]
    [MemberData(nameof(OpenAiBroken))]
    public async Task OpenAi_a_stream_that_errs_or_stops_short_fails(string _, string body)
    {
        using var http = new HttpClient(new Scripted(body));
        var provider = new OpenAiCompatibleProvider(http, Descriptor("openai-a", ProviderKind.OpenAiCompatible));

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => Drain(provider));
        Assert.Contains("openai-a", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. Either of the protocol's two endings is an ending.</summary>
    [Theory]
    [InlineData(SseTool + SseFinish + SseDone)]
    [InlineData(SseTool + SseFinish)]
    [InlineData(SseText + SseDone)]
    public async Task OpenAi_a_finished_stream_is_an_answer(string body)
    {
        using var http = new HttpClient(new Scripted(body));
        var provider = new OpenAiCompatibleProvider(http, Descriptor("openai-a", ProviderKind.OpenAiCompatible));

        var events = await Drain(provider);

        Assert.NotEmpty(events);
    }

    // ── through the engine ───────────────────────────────────────────────────

    /// <summary>
    /// THE MEASURED CASE: a write_file streamed, then the provider's error. The file is not written,
    /// the task does not complete, and the error is on the record.
    /// </summary>
    [Fact]
    public async Task A_tool_call_streamed_before_an_error_is_not_run()
    {
        using var fx = new EngineFixture();
        const string plan = """{"message":{"role":"assistant","content":"{\"disposition\":\"quick_action\",\"title\":\"write x\"}"},"done":true}""";
        const string done = """{"message":{"role":"assistant","content":"Done."},"done":true,"done_reason":"stop"}""";

        using var http = new HttpClient(new Scripted(plan, OllamaTool + "\n" + OllamaError, done));
        var provider = new OllamaNativeProvider(http, Descriptor("ollama-a", ProviderKind.OllamaNative));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write x");

        Assert.False(File.Exists(Path.Combine(fx.Root, "x.md")), "the call from the failed answer was run");
        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                     && e.Summary.Contains("generation failed", StringComparison.Ordinal));
    }
}
