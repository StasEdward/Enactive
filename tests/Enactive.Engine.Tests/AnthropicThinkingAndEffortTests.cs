namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// Run ddca5350, 2026-09-28: a combined review on claude-sonnet-5 spent 5,501 and then 11,002 output tokens
/// and returned no text. A Claude 5 model reasons by default (adaptive thinking), those tokens count against
/// max_tokens, and by default the blocks come back with their text omitted - which this adapter did not read
/// at all, so the log showed a silent reply. It now reads them, gives reasoning room of its own, and can ask
/// for a lower effort.
/// </summary>
public sealed class AnthropicThinkingAndEffortTests
{
    private static ProviderDescriptor Descriptor(string? effort = null, int? allowance = null)
        => new("p", "p", ProviderKind.Anthropic, "https://test.invalid", null, [], ReasoningTokenAllowance: allowance, Effort: effort);

    private static string Response(string content)
        => """{"content":[""" + content + """],"stop_reason":"max_tokens","usage":{"input_tokens":10,"output_tokens":11002}}""";

    [Fact]
    public async Task Reasoning_whose_text_was_omitted_is_still_reported()
    {
        using var handler = new Reply(Response("""{"type":"thinking","thinking":"","signature":"sig"}"""));
        using var http = new HttpClient(handler);
        var completion = await new AnthropicProvider(http, Descriptor()).CompleteAsync(new ChatRequest("m", [ChatMessage.User("review")]), default);

        Assert.Null(completion.Message.Content);
        Assert.Equal("(1 reasoning block(s); the provider returned no text for them)", completion.Thinking);
    }

    [Fact]
    public async Task Reasoning_text_that_was_returned_is_kept_apart_from_the_answer()
    {
        using var handler = new Reply(Response("""{"type":"thinking","thinking":"weighing claim 3","signature":"s"},{"type":"redacted_thinking","data":"x"},{"type":"text","text":"{}"}"""));
        using var http = new HttpClient(handler);
        var completion = await new AnthropicProvider(http, Descriptor()).CompleteAsync(new ChatRequest("m", [ChatMessage.User("review")]), default);

        Assert.Equal("{}", completion.Message.Content);
        Assert.Equal("weighing claim 3", completion.Thinking);
    }

    [Fact]
    public void Reasoning_has_room_of_its_own_unless_the_provider_says_otherwise()
    {
        using var http = new HttpClient();
        var request = new ChatRequest("m", []);
        Assert.Equal(8192, new AnthropicProvider(http, Descriptor()).ReasoningAllowance(request));
        Assert.Equal(0, new AnthropicProvider(http, Descriptor(allowance: 0)).ReasoningAllowance(request));
    }

    [Fact]
    public async Task Effort_goes_in_output_config_beside_the_response_format()
    {
        using var handler = new Reply(Response("""{"type":"text","text":"{}"}"""));
        using var http = new HttpClient(handler);
        await new AnthropicProvider(http, Descriptor(effort: "medium"))
            .CompleteAsync(new ChatRequest("m", [ChatMessage.User("review")], ResponseSchema: """{"type":"object","properties":{"verdict":{"type":"string"}}}"""), default);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var config = body.RootElement.GetProperty("output_config");
        Assert.Equal("medium", config.GetProperty("effort").GetString());
        Assert.Equal("json_schema", config.GetProperty("format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task No_effort_configured_sends_none()
    {
        using var handler = new Reply(Response("""{"type":"text","text":"ok"}"""));
        using var http = new HttpClient(handler);
        await new AnthropicProvider(http, Descriptor()).CompleteAsync(new ChatRequest("m", [ChatMessage.User("hi")]), default);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task A_model_that_refuses_effort_is_asked_again_without_it_and_not_asked_with_it_again()
    {
        using var handler = new Reply(Response("""{"type":"text","text":"ok"}"""),
            refusal: """{"error":{"type":"invalid_request_error","message":"output_config.effort: not supported for this model"}}""", refuseCount: 1);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, Descriptor(effort: "low"));
        await provider.CompleteAsync(new ChatRequest("m", [ChatMessage.User("hi")]), default);
        await provider.CompleteAsync(new ChatRequest("m", [ChatMessage.User("hi")]), default);

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("\"effort\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"effort\"", handler.Bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("\"effort\"", handler.Bodies[2], StringComparison.Ordinal);
    }

    private sealed class Reply(string ok, string? refusal = null, int refuseCount = 0) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var failed = Bodies.Count <= refuseCount;
            return new(failed ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                { Content = new StringContent(failed ? refusal! : ok, Encoding.UTF8, "application/json") };
        }
    }
}
