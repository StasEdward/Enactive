namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// Asking a provider to answer in a shape — and never depending on it having done so.
///
/// <para><c>FIX_PLAN.md</c> §9c: there is no single field for this. Anthropic takes
/// <c>output_config.format</c>, Ollama takes <c>format</c>, OpenAI-style endpoints take
/// <c>response_format</c>, and "OpenAI-compatible" is a family rather than a specification — an
/// arbitrary gateway may ignore the field or reject the whole request for it. So the seam is
/// <c>ChatRequest.ResponseSchema</c>: a REQUEST each adapter satisfies however it can, or not at
/// all.</para>
///
/// <para>Half of these tests are about the second half of that sentence. A schema must never be able
/// to make a call fail that would otherwise have worked, and it must never become the thing
/// correctness rests on — the reviewer parses, re-asks and fails closed exactly as it did before.
/// The same principle as an unreachable reviewer not counting as PASS.</para>
/// </summary>
public sealed class StructuredOutputTests
{
    private const string Schema = """{"type":"object","properties":{"verdict":{"type":"string"}},"required":["verdict"]}""";

    private const string AnthropicOk =
        """{"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"{\"verdict\":\"pass\",\"notes\":\"ok\"}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";

    private const string OpenAiOk =
        """{"choices":[{"message":{"role":"assistant","content":"{\"verdict\":\"pass\",\"notes\":\"ok\"}"},"finish_reason":"stop"}]}""";

    /// <summary>Answers with a canned body, keeps every request body, and can refuse the first N.</summary>
    private sealed class Recording : HttpMessageHandler
    {
        private readonly string _body;
        private readonly int _refuseFirst;
        private readonly string _refusal;
        private int _seen;

        public Recording(string body, int refuseFirst = 0, string refusal = """{"error":{"message":"unknown field response_format"}}""")
        {
            _body = body;
            _refuseFirst = refuseFirst;
            _refusal = refusal;
        }

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            return ++_seen <= _refuseFirst
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(_refusal, Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json")
                };
        }
    }

    /// <summary>
    /// The value at a path in a request body, checked for its KIND at every step.
    ///
    /// <para>These tests checked that words were in the body: "response_format" and "json_schema".
    /// A body that said <c>"response_format":"json_schema"</c> - a string where the API wants an
    /// object - passed them, and a deliberately broken serializer doing exactly that stayed green
    /// (Docs/PROVIDERS_AGENTS_TOOLS_TESTS_REVIEW_2026-09-24.md #4). The shape IS the contract here.</para>
    /// </summary>
    private static JsonElement At(string body, params string[] path)
    {
        var at = JsonDocument.Parse(body).RootElement;
        foreach (var step in path)
        {
            Assert.True(at.ValueKind == JsonValueKind.Object, $"'{step}' is under a {at.ValueKind}, not an object");
            Assert.True(at.TryGetProperty(step, out at), $"no '{step}' in the request body");
        }
        return at;
    }

    /// <summary>The schema the request asked for, arrived as a schema: an object with its property and its required list.</summary>
    private static void IsTheSchema(JsonElement schema)
    {
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("properties").GetProperty("verdict").ValueKind);
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), r => r.GetString() == "verdict");
    }

    private static ProviderDescriptor Descriptor(string id, ProviderKind kind, string model)
        => new(id, id, kind, "https://" + id + ".test", "key", new[] { model });

    // ── each adapter's own field ────────────────────────────────────────────

    [Fact]
    public async Task Anthropic_sends_the_schema_as_output_config_format()
    {
        var handler = new Recording(AnthropicOk);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://anthropic.test") };

        var provider = new AnthropicProvider(http, Descriptor("anthropic-a", ProviderKind.Anthropic, "claude-a"));

        await provider.CompleteAsync(
            new ChatRequest("claude-a", new[] { ChatMessage.User("judge it") }, ResponseSchema: Schema),
            CancellationToken.None);

        var sent = Assert.Single(handler.Bodies);
        Assert.Equal(JsonValueKind.Object, At(sent, "output_config", "format").ValueKind);
        Assert.Equal("json_schema", At(sent, "output_config", "format", "type").GetString());
        IsTheSchema(At(sent, "output_config", "format", "schema"));
    }

    [Fact]
    public async Task An_openai_compatible_endpoint_gets_response_format()
    {
        var handler = new Recording(OpenAiOk);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://openai.test") };

        var provider = new OpenAiCompatibleProvider(
            http, Descriptor("openai-a", ProviderKind.OpenAiCompatible, "gpt-a"));

        await provider.CompleteAsync(
            new ChatRequest("gpt-a", new[] { ChatMessage.User("judge it") }, ResponseSchema: Schema),
            CancellationToken.None);

        var sent = Assert.Single(handler.Bodies);
        Assert.Equal(JsonValueKind.Object, At(sent, "response_format").ValueKind);
        Assert.Equal("json_schema", At(sent, "response_format", "type").GetString());
        Assert.Equal(JsonValueKind.Object, At(sent, "response_format", "json_schema").ValueKind);
        IsTheSchema(At(sent, "response_format", "json_schema", "schema"));
    }

    [Fact]
    public async Task Ollama_gets_the_schema_itself_and_never_the_bare_word_json()
    {
        var handler = new Recording("""{"message":{"role":"assistant","content":"{}"},"done":true}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://ollama.test") };

        var provider = new OllamaNativeProvider(
            http, Descriptor("ollama-a", ProviderKind.OllamaNative, "qwen-a"));

        await provider.CompleteAsync(
            new ChatRequest("qwen-a", new[] { ChatMessage.User("judge it") }, ResponseSchema: Schema),
            CancellationToken.None);

        var sent = Assert.Single(handler.Bodies);

        // The schema, not "json". format:"json" with no schema is the trap §9c names: a small model
        // then returns valid JSON with invented keys, which is worse than prose because it parses.
        IsTheSchema(At(sent, "format"));
    }

    /// <summary>No schema asked for, nothing added — the field is opt-in, not a new default.</summary>
    [Fact]
    public async Task A_request_without_a_schema_is_unchanged()
    {
        var handler = new Recording(OpenAiOk);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://openai.test") };

        var provider = new OpenAiCompatibleProvider(
            http, Descriptor("openai-b", ProviderKind.OpenAiCompatible, "gpt-b"));

        await provider.CompleteAsync(
            new ChatRequest("gpt-b", new[] { ChatMessage.User("hello") }), CancellationToken.None);

        Assert.DoesNotContain("response_format", Assert.Single(handler.Bodies));
    }

    // ── and the half that matters more: it cannot break anything ────────────

    /// <summary>
    /// An endpoint that rejects the field gets the same request again without it. The worst case of
    /// asking is one wasted round trip — never a failed run.
    /// </summary>
    [Fact]
    public async Task An_endpoint_that_refuses_the_schema_is_asked_again_without_it()
    {
        var handler = new Recording(OpenAiOk, refuseFirst: 1);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://openai.test") };

        var provider = new OpenAiCompatibleProvider(
            http, Descriptor("openai-c", ProviderKind.OpenAiCompatible, "gpt-c"));

        var completion = await provider.CompleteAsync(
            new ChatRequest("gpt-c", new[] { ChatMessage.User("judge it") }, ResponseSchema: Schema),
            CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("response_format", handler.Bodies[0]);
        Assert.DoesNotContain("response_format", handler.Bodies[1]);
        Assert.Contains("pass", completion.Message.Content);
    }

    /// <summary>
    /// And it is asked once, not once per call. A discovery about an endpoint is a fact about the
    /// endpoint; paying a 400 for it on every review would be a tax on a feature that is optional.
    /// </summary>
    [Fact]
    public async Task An_endpoint_that_refused_once_is_not_asked_again()
    {
        var handler = new Recording(OpenAiOk, refuseFirst: 1);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://openai.test") };

        var provider = new OpenAiCompatibleProvider(
            http, Descriptor("openai-d", ProviderKind.OpenAiCompatible, "gpt-d"));

        var request = new ChatRequest("gpt-d", new[] { ChatMessage.User("judge it") }, ResponseSchema: Schema);

        await provider.CompleteAsync(request, CancellationToken.None);
        await provider.CompleteAsync(request, CancellationToken.None);

        // First call: refused, retried. Second call: went straight out without the schema.
        Assert.Equal(3, handler.Bodies.Count);
        Assert.DoesNotContain("response_format", handler.Bodies[2]);
    }

    [Fact]
    public async Task Anthropic_drops_the_schema_when_it_is_refused()
    {
        var handler = new Recording(AnthropicOk, refuseFirst: 1,
            refusal: """{"error":{"message":"output_config.format is not supported"}}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://anthropic.test") };

        var provider = new AnthropicProvider(http, Descriptor("anthropic-e", ProviderKind.Anthropic, "claude-e"));

        await provider.CompleteAsync(
            new ChatRequest("claude-e", new[] { ChatMessage.User("judge it") }, ResponseSchema: Schema),
            CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("output_config", handler.Bodies[0]);
        Assert.DoesNotContain("output_config", handler.Bodies[1]);
    }

    /// <summary>A schema that is not valid JSON is simply not sent. A bad schema must not fail a run.</summary>
    [Fact]
    public async Task An_unparseable_schema_is_left_off_rather_than_thrown()
    {
        var handler = new Recording(OpenAiOk);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://openai.test") };

        var provider = new OpenAiCompatibleProvider(
            http, Descriptor("openai-f", ProviderKind.OpenAiCompatible, "gpt-f"));

        await provider.CompleteAsync(
            new ChatRequest("gpt-f", new[] { ChatMessage.User("judge it") }, ResponseSchema: "{ not a schema"),
            CancellationToken.None);

        Assert.DoesNotContain("response_format", Assert.Single(handler.Bodies));
    }

    // ── the reviewer asks for one, and still does not trust it ──────────────

    /// <summary>The reviewer asks every provider for the verdict shape.</summary>
    [Fact]
    public void The_reviewer_asks_for_the_verdict_shape()
    {
        Assert.Contains("verdict", Reviewer.VerdictSchema);
        Assert.Contains("\"enum\"", Reviewer.VerdictSchema);
        Assert.Contains("notes", Reviewer.VerdictSchema);

        // It has to BE a schema, or every adapter quietly drops it.
        var parsed = System.Text.Json.JsonDocument.Parse(Reviewer.VerdictSchema);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, parsed.RootElement.ValueKind);
    }

    /// <summary>
    /// And it actually asks: the review request carries the schema, so a provider that can hold the
    /// model to it gets the chance. Without this the constant above would be decoration.
    /// </summary>
    [Fact]
    public async Task The_review_request_carries_the_schema()
    {
        using var fx = new EngineFixture { ShortReview = false };

        var agent = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Calls1("write_file", """{"path":"a.txt","content":"x"}"""),
            Turn.Says("Done."));

        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await fx.RunAsync(
            fx.Build(agent, router: Routers.WithReviewer(), reviewProvider: reviewer),
            "do the thing");

        var review = Assert.Single(reviewer.Requests);
        Assert.Equal(Reviewer.VerdictSchema, review.ResponseSchema);
    }

    /// <summary>
    /// And the PLANNER does not, deliberately. §9c: the planner is the one place where the quality
    /// of the reasoning matters more than the shape of the answer, and constrained decoding on a
    /// 12-14B model can eat exactly what we go there for. The reviewer has nothing to reason about,
    /// which is why it went first. Measure before changing this.
    /// </summary>
    [Fact]
    public async Task The_planner_is_not_given_a_schema()
    {
        using var fx = new EngineFixture();

        var planner = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(planner), "do the thing");

        Assert.All(planner.Requests, r => Assert.Null(r.ResponseSchema));
    }

    /// <summary>
    /// The one that matters most. A provider that IGNORES the schema and answers prose is exactly
    /// where this was before, and the reviewer must behave exactly as it did: re-ask, and on a
    /// second unreadable answer fail closed. A schema makes the bad path rarer, never absent.
    /// </summary>
    [Fact]
    public async Task A_provider_that_ignores_the_schema_still_fails_closed()
    {
        using var fx = new EngineFixture { ShortReview = false };

        var agent = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Calls1("write_file", """{"path":"a.txt","content":"x"}"""),
            Turn.Says("Done."));

        // Prose, twice - a model that was handed a schema and paid it no attention.
        var reviewer = new FakeChatProvider(Turn.Says("Looks fine to me."))
        {
            WhenExhausted = Turn.Says("Still looks fine.")
        };

        var events = await fx.RunAsync(
            fx.Build(agent, router: Routers.WithReviewer(), reviewProvider: reviewer),
            "do the thing");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => (e.Summary ?? "").Contains("did not return a verdict"));
    }
}
