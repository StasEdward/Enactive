namespace Enactive.Engine.Tests;

using System.Net;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class OllamaUsageTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, true)]
    [InlineData(80, true)]
    public async Task Native_usage_preserves_total_and_only_calibrates_with_explicit_cache_contract(int? cached, bool calibrate)
    {
        var extra = cached is { } n ? $",\"prompt_eval_cached_count\":{n}" : "";
        var body = "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true,\"prompt_eval_count\":100,\"eval_count\":3" + extra + "}\n";
        using var http = new HttpClient(new Reply(body));
        var provider = new OllamaNativeProvider(http, new("local", "local", ProviderKind.OllamaNative, "http://test.invalid", null, []));
        var completion = await provider.CompleteAsync(new("model", []), default);
        var events = new List<ChatStreamEvent>();
        await foreach (var ev in provider.StreamChatAsync(new("model", []), default)) events.Add(ev);
        var usage = Assert.Single(events.OfType<UsageDelta>());
        Assert.Equal(100, completion.PromptTokens);
        Assert.Equal(100, usage.PromptTokens); // Cached share is not added a second time.
        Assert.Equal(cached, completion.CachedPromptTokens);
        Assert.Equal(cached, usage.CachedPromptTokens);
        Assert.Equal(calibrate, completion.PromptTokensIncludeCache);
        Assert.Equal(calibrate, usage.PromptTokensIncludeCache);
    }

    [Fact]
    public async Task Partial_prompt_usage_does_not_disable_context_trimming()
    {
        using var fx = new EngineFixture();
        var big = new string('x', 24000);
        var script = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Calls1("write_file", $$"""{"path":"one.txt","content":"{{big}}"}""", "c1").Reporting(1, 10),
            Turn.Calls1("read_file", """{"path":"one.txt"}""", "c2").Reporting(1, 10),
            Turn.Says("Done.")) { Window = 4096 };
        var events = await fx.RunAsync(fx.Build(new PartialUsage(script)), "do the thing");
        Assert.True(events.Has(EventKind.ContextTrimmed));
        Assert.True(events.Has(EventKind.TaskCompleted));
        Assert.Contains(events.OfKind(EventKind.UsageReported), e => e.Usage()?.In == 1);
    }

    private sealed class PartialUsage(FakeChatProvider inner) : IChatProvider
    {
        public int? ContextWindow(ChatRequest request) => inner.ContextWindow(request);
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct) => inner.CompleteAsync(request, ct);
        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var ev in inner.StreamChatAsync(request, ct))
                yield return ev is UsageDelta usage ? usage with { PromptTokensIncludeCache = false } : ev;
        }
    }

    private sealed class Reply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
