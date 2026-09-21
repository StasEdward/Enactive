namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// How much of a prompt the provider served from its own cache, which nothing read.
///
/// <para><b>Measured 2026-09-21.</b> A twelve-minute task spent 31,391,109 prompt tokens over 333
/// completions and not one of them reported a cached figure — because only the Anthropic adapter
/// looked for one, and DeepSeek, Jan, llama.cpp and LM Studio all come through the
/// OpenAI-compatible one. A hit costs roughly a tenth of a miss, so the bill for that run was
/// unknown within a factor of ten.</para>
///
/// <para>The rest of the pipeline was already built for this and idle at both ends:
/// <c>ChatCompletion.CachedPromptTokens</c>, <c>UsageDelta</c>, <c>RunUsage.CachedPercent</c>,
/// the summing in <c>RunRecorder</c>. What was missing was a provider that reads the number and
/// a report that prints it.</para>
///
/// <para>Verified live against DeepSeek the same day, which answered 87% — so the volume was real
/// and the alarm about the cost was not.</para>
/// </summary>
public sealed class CachedPromptTokensTests
{
    private static ProviderDescriptor Descriptor()
        => new("p", "Provider", ProviderKind.OpenAiCompatible, "https://provider.test", "test-key",
               new[] { "test-model" }, null, null);

    private static async Task<ChatCompletion> AnswerAsync(string usageJson)
    {
        const string choices =
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]""";

        var body = choices + ",\"usage\":" + usageJson + "}";

        using var http = new HttpClient(new CannedHandler(body))
        {
            BaseAddress = new Uri("https://provider.test")
        };

        return await new OpenAiCompatibleProvider(http, Descriptor()).CompleteAsync(
            new ChatRequest("test-model", new[] { ChatMessage.User("go") }), CancellationToken.None);
    }

    /// <summary>DeepSeek, verified against a live response: flat, beside the miss count.</summary>
    [Fact]
    public async Task The_flat_hit_count_is_read()
    {
        var completion = await AnswerAsync(
            """{"prompt_tokens":3220,"completion_tokens":78,"prompt_cache_hit_tokens":2944,"prompt_cache_miss_tokens":276}""");

        Assert.Equal(3220, completion.PromptTokens);
        Assert.Equal(2944, completion.CachedPromptTokens);
    }

    /// <summary>OpenAI and the adapters that copy its shape: nested under the details object.</summary>
    [Fact]
    public async Task The_nested_details_count_is_read()
    {
        var completion = await AnswerAsync(
            """{"prompt_tokens":3220,"completion_tokens":78,"prompt_tokens_details":{"cached_tokens":1024}}""");

        Assert.Equal(1024, completion.CachedPromptTokens);
    }

    /// <summary>
    /// NULL AND ZERO ARE DIFFERENT ANSWERS. A provider that cached nothing said so; one that does
    /// not measure caching said nothing. Reading the second as the first would put a confident 0%
    /// on a run nobody measured — which is the shape of the defect this fixes, inverted.
    /// </summary>
    [Fact]
    public async Task A_provider_that_does_not_report_caching_reports_nothing()
    {
        var completion = await AnswerAsync("""{"prompt_tokens":3220,"completion_tokens":78}""");

        Assert.Equal(3220, completion.PromptTokens);
        Assert.Null(completion.CachedPromptTokens);
    }

    [Fact]
    public async Task A_provider_that_cached_nothing_says_zero()
    {
        var completion = await AnswerAsync(
            """{"prompt_tokens":3220,"completion_tokens":78,"prompt_cache_hit_tokens":0}""");

        Assert.Equal(0, completion.CachedPromptTokens);
    }

    /// <summary>The cached tokens are PART of the prompt count, not on top of it.</summary>
    [Fact]
    public void The_share_is_of_the_prompt_the_provider_already_counted()
    {
        var usage = new Enactive.Core.History.RunUsage(21007, 1605) { CachedPromptTokens = 18300 };

        Assert.Equal(87, usage.CachedPercent);
    }

    /// <summary>And with nothing reported there is no percentage to state.</summary>
    [Fact]
    public void With_nothing_reported_there_is_no_share()
        => Assert.Null(new Enactive.Core.History.RunUsage(21007, 1605).CachedPercent);

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
