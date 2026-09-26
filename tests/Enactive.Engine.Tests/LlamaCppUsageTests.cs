namespace Enactive.Engine.Tests;

using System.Net;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

public sealed class LlamaCppUsageTests
{
    // Wire shape measured on local b11160-70c4e1582, qwen3.6-35b-a3b.
    // On a cache hit timings.prompt_n counts only fresh evaluation, unlike usage.prompt_tokens.
    [Theory]
    [InlineData(false, 0, 671)]
    [InlineData(false, 667, 4)]
    [InlineData(true, 0, 671)]
    [InlineData(true, 667, 4)]
    public async Task Context_uses_full_prompt_not_fresh_evaluation(bool stream, int cached, int evaluated)
    {
        var metrics = System.Text.Json.JsonSerializer.Serialize(new
        {
            usage = new { prompt_tokens = 671, completion_tokens = 2, total_tokens = 673,
                prompt_tokens_details = new { cached_tokens = cached } },
            timings = new { cache_n = cached, prompt_n = evaluated, prompt_ms = 46.851,
                predicted_n = 2, predicted_ms = 21.347 }
        })[1..^1];
        var body = stream
            ? "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: {\"choices\":[]," + metrics + "}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]," + metrics + "}";
        using var http = new HttpClient(new Reply(body));
        var provider = new OpenAiCompatibleProvider(http,
            new("llama.cpp", "llama.cpp", ProviderKind.OpenAiCompatible, "http://test.invalid/v1", null, []));
        var request = new ChatRequest("qwen3.6-35b-a3b", [ChatMessage.User("accounting probe")]);
        if (stream)
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var ev in provider.StreamChatAsync(request, default)) events.Add(ev);
            var usage = Assert.Single(events.OfType<UsageDelta>());
            Assert.Equal(671, usage.PromptTokens);
            Assert.Equal(2, usage.CompletionTokens);
            Assert.Equal(cached, usage.CachedPromptTokens);
            Assert.True(usage.PromptTokensIncludeCache);
        }
        else
        {
            var completion = await provider.CompleteAsync(request, default);
            Assert.Equal(671, completion.PromptTokens);
            Assert.Equal(2, completion.CompletionTokens);
            Assert.Equal(cached, completion.CachedPromptTokens);
            Assert.True(completion.PromptTokensIncludeCache);
        }
    }

    private sealed class Reply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
