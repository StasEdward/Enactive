namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class ReviewGrammarTests
{
    private const string TooLarge = """{"error":{"type":"invalid_request_error","message":"The compiled grammar is too large, which would cause performance issues. Simplify your tool schemas or reduce the number of strict tools."}}""";
    private const string Ok = """{"content":[{"type":"text","text":"{}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complexity_retry_keeps_schema_in_prompt_and_does_not_disable_later_grammars(bool stream)
    {
        using var handler = new Reply(TooLarge, refuseCount: 1, stream);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, new("p", "p", ProviderKind.Anthropic, "https://test.invalid", null, []));
        var request = new ChatRequest("model", [ChatMessage.User("Review synthetic content")], ResponseSchema: Reviewer.CombinedSchema);
        async Task Send()
        {
            if (stream) await foreach (var item in provider.StreamChatAsync(request, default)) { }
            else await provider.CompleteAsync(request, default);
        }
        await Send();
        await Send();
        Assert.Equal(3, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        using var fallback = JsonDocument.Parse(handler.Bodies[1]);
        using var next = JsonDocument.Parse(handler.Bodies[2]);
        Assert.True(first.RootElement.TryGetProperty("output_config", out _));
        Assert.False(fallback.RootElement.TryGetProperty("output_config", out _));
        Assert.Contains(Reviewer.CombinedSchema, fallback.RootElement.GetProperty("system")[0].GetProperty("text").GetString()!);
        Assert.True(next.RootElement.TryGetProperty("output_config", out _));
        Assert.Single(request.Messages); // Request/transcript was not mutated.
    }

    [Theory]
    [InlineData("""{"error":{"type":"invalid_request_error","message":"Context length exceeded"}}""", 1)]
    [InlineData("""{"error":{"type":"invalid_request_error","message":"Invalid messages"}}""", 1)]
    [InlineData(TooLarge, 2)]
    public async Task Retry_is_bounded_and_unrelated_400_is_not_a_capability_signal(string error, int attempts)
    {
        using var handler = new Reply(error, refuseCount: 20);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, new("p", "p", ProviderKind.Anthropic, "https://test.invalid", null, []));
        var request = new ChatRequest("model", [], ResponseSchema: Reviewer.CombinedSchema);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(request, default));
        Assert.Equal(attempts, handler.Bodies.Count);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(request, default));
        using var next = JsonDocument.Parse(handler.Bodies[attempts]);
        Assert.True(next.RootElement.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task Reviewer_still_rejects_invalid_enum_and_requests_correction()
    {
        var bad = JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("content"), "run").Text!)!;
        bad["assessments"]!["report"]!["verdict"] = "probably-fine";
        var provider = new FakeChatProvider(Turn.Says(bad.ToJsonString()), Turn.Says(bad.ToJsonString()));
        var result = await new Reviewer().ReviewWithProofAsync("review", "done", new ExecutionJournal().Describe(), [], [],
            RequestObligations.Create("Review content"), provider, "model", default);
        Assert.False(result.Pass);
        Assert.NotNull(result.IncompleteReason);
        Assert.Equal(Reviewer.CombinedWireSchema, provider.Requests[0].ResponseSchema);
        Assert.Contains("$.assessments.report.verdict", provider.Requests[1].Messages.Last().Content!);
    }

    private sealed class Reply(string refusal, int refuseCount, bool stream = false) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var failed = Bodies.Count <= refuseCount;
            var body = failed ? refusal : stream
                ? "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1}}}\n\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"{}\"}}\n\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\ndata: {\"type\":\"message_stop\"}\n\n" : Ok;
            return new(failed ? HttpStatusCode.BadRequest : HttpStatusCode.OK) {
                Content = new StringContent(body, Encoding.UTF8, !failed && stream ? "text/event-stream" : "application/json") };
        }
    }
}
