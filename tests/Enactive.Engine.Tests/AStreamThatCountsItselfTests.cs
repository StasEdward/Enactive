namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// A streamed call asks for its own token counts, or it has none.
///
/// <para><b>The measurement.</b> 2026-09-23 20:44, a run whose worker was a local
/// <c>llama.cpp</c>: two steps, three and a half minutes, and not one <c>UsageReported</c> event
/// from it. The only usage in the whole run came from the planner and the reviewer, which go
/// NON-streamed and therefore always carry it — so the work-split panel reported the run as 100%
/// cloud while the work was happening on this machine.</para>
///
/// <para><b>Why it was invisible until a local model was bound to a phase.</b> A streamed response
/// carries no usage block unless <c>stream_options.include_usage</c> asks for it — that is the
/// specification. DeepSeek sends it anyway, beyond the spec, and DeepSeek was the worker in every
/// run before this one.</para>
///
/// <para>The field is sent until an endpoint refuses it, then never again for that endpoint, which
/// is the same bargain <c>response_format</c> already makes: a field that might not be understood
/// must never be able to fail a call that would otherwise have worked.</para>
/// </summary>
public sealed class AStreamThatCountsItselfTests
{
    /// <summary>What a server sends back: a content chunk, then a usage-only chunk, then DONE.</summary>
    private const string StreamWithUsage =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n"
        + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":4321,\"completion_tokens\":77}}\n"
        + "data: [DONE]\n";

    /// <summary>The same answer, unstreamed - one object, usage included without being asked.</summary>
    private const string Whole =
        """{"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4321,"completion_tokens":77}}""";

    private sealed class Recording : HttpMessageHandler
    {
        private readonly int _refuseFirst;
        private int _seen;

        public Recording(int refuseFirst = 0) => _refuseFirst = refuseFirst;

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);

            if (++_seen <= _refuseFirst)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"unrecognized field stream_options"}}""",
                        Encoding.UTF8, "application/json")
                };

            // A real endpoint answers a streamed request with an event stream and an unstreamed one
            // with a JSON object. Answering both the same way would let a test pass over a call
            // shaped wrongly.
            return body.Contains("\"stream\":true", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StreamWithUsage, Encoding.UTF8, "text/event-stream")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Whole, Encoding.UTF8, "application/json")
                };
        }
    }

    private static ProviderDescriptor Descriptor(string id, string model)
        => new(id, id, ProviderKind.OpenAiCompatible, "http://127.0.0.1:8080/v1", "key", new[] { model });

    private static async Task<List<ChatStreamEvent>> DrainAsync(IChatProvider provider, string model)
    {
        var events = new List<ChatStreamEvent>();

        await foreach (var ev in provider.StreamChatAsync(
                           new ChatRequest(model, new[] { ChatMessage.User("hello") }),
                           CancellationToken.None))
            events.Add(ev);

        return events;
    }

    [Fact]
    public async Task A_streamed_call_asks_for_usage()
    {
        var handler = new Recording();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080") };

        var events = await DrainAsync(
            new OpenAiCompatibleProvider(http, Descriptor("local-a", "qwen-a")), "qwen-a");

        var sent = Assert.Single(handler.Bodies);

        // The value, not the words: include_usage must be the boolean true, inside an object.
        var options = System.Text.Json.JsonDocument.Parse(sent).RootElement.GetProperty("stream_options");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, options.ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.True, options.GetProperty("include_usage").ValueKind);

        // And the usage-only chunk at the end of the stream is read: it carries no choices, which
        // is exactly the shape a parser keyed on choices would drop on the floor.
        var usage = Assert.Single(events.OfType<UsageDelta>());
        Assert.Equal(4321, usage.PromptTokens);
        Assert.Equal(77, usage.CompletionTokens);
    }

    /// <summary>
    /// THE BOUNDARY. "OpenAI-compatible" is a family, not a specification. An endpoint that has
    /// never heard of the field answers 400, and the call is sent again without it — so the worst
    /// case is the behaviour this had before the field existed, plus one round trip, once.
    /// </summary>
    [Fact]
    public async Task An_endpoint_that_refuses_it_still_works()
    {
        var handler = new Recording(refuseFirst: 1);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080") };

        var events = await DrainAsync(
            new OpenAiCompatibleProvider(http, Descriptor("picky-a", "qwen-b")), "qwen-b");

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("stream_options", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("stream_options", handler.Bodies[1], StringComparison.Ordinal);

        Assert.Contains(events, e => e is TextDelta);
    }

    /// <summary>
    /// And it is remembered: the second call to the same endpoint does not spend the round trip
    /// again. A fact about the endpoint, not about one call — the same table as the schema.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_remembered_for_that_endpoint()
    {
        var handler = new Recording(refuseFirst: 1);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080") };

        var provider = new OpenAiCompatibleProvider(http, Descriptor("picky-b", "qwen-c"));

        await DrainAsync(provider, "qwen-c");
        handler.Bodies.Clear();

        await DrainAsync(provider, "qwen-c");

        var second = Assert.Single(handler.Bodies);
        Assert.DoesNotContain("stream_options", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NON-streamed call does not ask: usage is in that response whatever anyone sends, and a
    /// field with no purpose is one more thing for a strict gateway to refuse.
    /// </summary>
    [Fact]
    public async Task A_call_that_is_not_streamed_does_not_ask()
    {
        var handler = new Recording();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080") };

        await new OpenAiCompatibleProvider(http, Descriptor("local-b", "qwen-d")).CompleteAsync(
            new ChatRequest("qwen-d", new[] { ChatMessage.User("hello") }), CancellationToken.None);

        Assert.DoesNotContain("stream_options", Assert.Single(handler.Bodies), StringComparison.Ordinal);
    }
}
