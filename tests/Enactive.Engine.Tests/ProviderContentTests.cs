namespace Enactive.Engine.Tests;

using System.Net;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.Core.Events;

public sealed class ProviderContentTests
{
    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.Anthropic)]
    public async Task Suspended_or_refused_worker_never_executes_pending_tools(ProviderKind kind)
    {
        using var fx = new EngineFixture();
        var body = kind == ProviderKind.OpenAiCompatible
            ? "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c\",\"function\":{\"name\":\"write_file\",\"arguments\":\"{\\\"path\\\":\\\"x.md\\\",\\\"content\\\":\\\"bad\\\"}\"}}]}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"refusal\":\"Cannot comply\"}}]}\n\n"
            : "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"c\",\"name\":\"write_file\",\"input\":{\"path\":\"x.md\",\"content\":\"bad\"}}}\n\n"
                + "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"pause_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n";
        using var handler = new Reply(body);
        using var http = new HttpClient(handler);
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"write x"}"""));
        var providers = new MapProviderFactory(Provider(http, kind), (Routers.PlannerProviderId, planner));
        var events = await fx.RunAsync(fx.Build(providers, EngineFixture.Role("developer"),
            router: Routers.WithPlannerOn()), "write x");
        Assert.False(File.Exists(Path.Combine(fx.Root, "x.md")));
        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
            && e.Summary.Contains(kind == ProviderKind.Anthropic ? "pause_turn" : "refusal"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Content_blocks_preserve_order_and_tool_calls(bool stream)
    {
        const string message = """
            {"content":[{"type":"text","text":"hello "},{"type":"text","text":"world"}],"refusal":null,"tool_calls":[{"index":0,"id":"c","type":"function","function":{"name":"read_file","arguments":"{}"}}]}
            """;
        using var handler = new Reply(OpenAi(message, stream));
        using var http = new HttpClient(handler);
        var provider = Provider(http, ProviderKind.OpenAiCompatible);
        if (stream)
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var e in provider.StreamChatAsync(new("m", []), default)) events.Add(e);
            Assert.Equal("hello world", string.Concat(events.OfType<TextDelta>().Select(e => e.Text)));
            Assert.Single(events.OfType<ToolCallDelta>());
            Assert.Single(events.OfType<FinishDelta>());
        }
        else
        {
            var result = await provider.CompleteAsync(new("m", []), default);
            Assert.Equal("hello world", result.Message.Content);
            Assert.Equal("read_file", Assert.Single(result.Message.ToolCalls!).Name);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Refusal_is_not_empty_success_or_retried(bool stream, bool block)
    {
        var message = block
            ? """{"content":[{"type":"text","text":"partial"},{"type":"refusal","refusal":"Cannot comply"}]}"""
            : """{"content":null,"refusal":"Cannot comply"}""";
        using var handler = new Reply(OpenAi(message, stream));
        using var http = new HttpClient(handler);
        var error = await Failure(Provider(http, ProviderKind.OpenAiCompatible), stream);
        Assert.Contains("refusal", error.Message);
        Assert.Contains("Cannot comply", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, "pause_turn")]
    [InlineData(true, "pause_turn")]
    [InlineData(false, "refusal")]
    [InlineData(true, "refusal")]
    public async Task Anthropic_nonfinal_turn_cannot_return_success(bool stream, string reason)
    {
        var body = stream
            ? "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"partial\"}}\n\n"
                + "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + reason + "\"}}\n\n"
                + "data: {\"type\":\"message_stop\"}\n\n"
            : "{\"content\":[{\"type\":\"text\",\"text\":\"partial\"}],\"stop_reason\":\"" + reason + "\"}";
        using var handler = new Reply(body);
        using var http = new HttpClient(handler);
        var error = await Failure(Provider(http, ProviderKind.Anthropic), stream);
        Assert.Contains(reason, error.Message);
        Assert.Equal(1, handler.Calls);
    }

    private static async Task<InvalidDataException> Failure(IChatProvider provider, bool stream)
        => await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            if (stream)
                await foreach (var e in provider.StreamChatAsync(new("m", []), default))
                    Assert.IsNotType<FinishDelta>(e);
            else await provider.CompleteAsync(new("m", []), default);
        });

    private static string OpenAi(string message, bool stream)
        => stream ? "data: {\"choices\":[{\"delta\":" + message + ",\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":" + message + ",\"finish_reason\":\"stop\"}]}";

    private static IChatProvider Provider(HttpClient http, ProviderKind kind)
    {
        var descriptor = new ProviderDescriptor("test", "Test", kind, "https://test.invalid", null, []);
        return new ResilientChatProvider(kind == ProviderKind.Anthropic
            ? new AnthropicProvider(http, descriptor) : new OpenAiCompatibleProvider(http, descriptor));
    }

    private sealed class Reply(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
