namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;

public sealed class EffectiveOutputLimitTests
{
    public static IEnumerable<object?[]> Limits()
    {
        (int? Request, int? Configured, int? Hard, int? Expected)[] cases =
        {
            (17, null, null, 17), // The reported native-only-MaxTokens regression.
            (null, 256, null, 256),
            (17, 256, null, 17),
            (256, 17, null, 256), // An explicit preference overrides the default on every adapter.
            (null, 17, 4096, 17), // A large remaining window must not raise a small preference.
            (null, 4096, 17, 17),
            (4096, 256, 17, 17),
            (17, 4096, 256, 17),
            (null, null, 17, 17),
            (null, null, null, null),
            (0, 17, null, 17),
            (-1, null, 17, 17),
            (null, -1, null, null)
        };
        foreach (var kind in Enum.GetValues<ProviderKind>())
        foreach (var stream in new[] { false, true })
        foreach (var c in cases)
            yield return new object?[] { kind, stream, c.Request, c.Configured, c.Hard,
                c.Expected ?? (kind == ProviderKind.Anthropic ? 32000 : null) };
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public async Task All_adapters_send_the_same_effective_output_limit(
        ProviderKind kind, bool stream, int? preferred, int? configured, int? hard, int? expected)
    {
        using var handler = new CaptureHandler(Response(kind, stream));
        using var http = new HttpClient(handler);
        var provider = Build(http, kind, configured);
        var request = new ChatRequest("model", new[] { ChatMessage.User("hi") },
            MaxTokens: preferred, OutputTokenLimit: hard);

        if (stream)
            await foreach (var _ in provider.StreamChatAsync(request, CancellationToken.None)) { }
        else
            await provider.CompleteAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(expected, Limit(body.RootElement, kind));
        if (kind == ProviderKind.OllamaNative && expected is null)
            Assert.False(body.RootElement.TryGetProperty("options", out _));
        if (kind == ProviderKind.OpenAiCompatible)
            Assert.False(body.RootElement.TryGetProperty("options", out _));
    }

    [Fact]
    public async Task Native_limit_coexists_with_temperature_and_context_window()
    {
        using var handler = new CaptureHandler(Response(ProviderKind.OllamaNative, false));
        using var http = new HttpClient(handler);
        await Build(http, ProviderKind.OllamaNative).CompleteAsync(
            new ChatRequest("model", new[] { ChatMessage.User("hi") },
                Temperature: 0.2, NumCtx: 8192, MaxTokens: 17), CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var options = body.RootElement.GetProperty("options");
        Assert.Equal(17, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0.2, options.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    [InlineData(ProviderKind.Anthropic)]
    public async Task An_invalid_hard_ceiling_is_rejected_before_sending(ProviderKind kind)
    {
        using var handler = new CaptureHandler(Response(kind, false));
        using var http = new HttpClient(handler);
        foreach (var invalid in new[] { 0, -1 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Build(http, kind).CompleteAsync(
                new ChatRequest("model", new[] { ChatMessage.User("hi") }, OutputTokenLimit: invalid),
                CancellationToken.None));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Anthropic_learns_a_model_cap_without_raising_a_smaller_request_limit()
    {
        using var handler = new CaptureHandler(Response(ProviderKind.Anthropic, false),
            firstError: "max_tokens must not be > 32");
        using var http = new HttpClient(handler);
        var provider = Build(http, ProviderKind.Anthropic, 256);
        var request = new ChatRequest("cap-test-" + Guid.NewGuid(), new[] { ChatMessage.User("hi") },
            OutputTokenLimit: 64);

        await provider.CompleteAsync(request, CancellationToken.None); // 64 -> retry with 32
        await provider.CompleteAsync(request, CancellationToken.None); // cached cap, no 400
        await provider.CompleteAsync(request with { OutputTokenLimit = 17 }, CancellationToken.None);

        Assert.Equal(new int?[] { 64, 32, 32, 17 }, handler.Bodies.Select(json =>
        {
            using var body = JsonDocument.Parse(json);
            return Limit(body.RootElement, ProviderKind.Anthropic);
        }));
    }

    private static IChatProvider Build(HttpClient http, ProviderKind kind, int? configured = null)
    {
        var descriptor = new ProviderDescriptor("test", "test", kind, "https://provider.test", null,
            new[] { "model" }, MaxTokens: configured);
        return kind switch
        {
            ProviderKind.OpenAiCompatible => new OpenAiCompatibleProvider(http, descriptor),
            ProviderKind.OllamaNative => new OllamaNativeProvider(http, descriptor),
            ProviderKind.Anthropic => new AnthropicProvider(http, descriptor),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static int? Limit(JsonElement body, ProviderKind kind)
    {
        if (kind == ProviderKind.OllamaNative)
            return body.TryGetProperty("options", out var options)
                && options.TryGetProperty("num_predict", out var limit) ? limit.GetInt32() : null;
        return body.TryGetProperty("max_tokens", out var max) ? max.GetInt32() : null;
    }

    private static string Response(ProviderKind kind, bool stream) => kind switch
    {
        ProviderKind.OllamaNative => """{"message":{"role":"assistant","content":"ok"},"done":true}""" + "\n",
        ProviderKind.Anthropic => """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""",
        _ when stream => "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        _ => """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""
    };

    private sealed class CaptureHandler(string response, string? firstError = null) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var error = Bodies.Count == 1 ? firstError : null;
            return new HttpResponseMessage(error is null ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = new StringContent(error ?? response, Encoding.UTF8, "application/json")
            };
        }
    }
}
