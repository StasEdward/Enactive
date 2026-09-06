namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// Settings that a person can fill in on the provider editor have to reach the wire. Two of the
/// three adapters dropped the custom headers, and the OpenAI-compatible one dropped the token budget
/// as well: the field saved, the UI showed it, and the request went out without it. The failure then
/// surfaced somewhere else entirely — a 401 from a gateway, a truncated answer, a bill on the wrong
/// account — with nothing pointing back at the setting.
///
/// These tests look at the request that was actually built, which is the only place the question can
/// be answered.
/// </summary>
public sealed class ProviderSettingsTests
{
    private static readonly Dictionary<string, string> CustomHeaders = new()
    {
        ["x-gateway-token"] = "gw-secret",
        ["OpenAI-Organization"] = "org-42"
    };

    private static ProviderDescriptor Descriptor(
        ProviderKind kind,
        IReadOnlyDictionary<string, string>? headers = null,
        int? maxTokens = null)
        => new("p", "Provider", kind, "https://provider.test", "test-key",
               new[] { "test-model" }, headers, maxTokens);

    private static ChatRequest Request(int? maxTokens = null)
        => new("test-model", new[] { ChatMessage.User("do the thing") }, MaxTokens: maxTokens);

    // ---- custom headers, per adapter -------------------------------------------------

    [Fact]
    public async Task OpenAiCompatible_sends_the_configured_headers()
    {
        var sent = await CaptureAsync(
            http => new OpenAiCompatibleProvider(
                http, Descriptor(ProviderKind.OpenAiCompatible, CustomHeaders)),
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");

        Assert.Equal("gw-secret", Header(sent, "x-gateway-token"));
        Assert.Equal("org-42", Header(sent, "OpenAI-Organization"));
    }

    [Fact]
    public async Task OllamaNative_sends_the_configured_headers()
    {
        var sent = await CaptureAsync(
            http => new OllamaNativeProvider(
                http, Descriptor(ProviderKind.OllamaNative, CustomHeaders)),
            """{"message":{"role":"assistant","content":"ok"},"done":true}""");

        Assert.Equal("gw-secret", Header(sent, "x-gateway-token"));
    }

    [Fact]
    public async Task Anthropic_sends_the_configured_headers_alongside_its_own()
    {
        var sent = await CaptureAsync(
            http => new AnthropicProvider(
                http, Descriptor(ProviderKind.Anthropic, CustomHeaders)),
            """
            {"id":"m","type":"message","role":"assistant",
             "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}
            """);

        Assert.Equal("gw-secret", Header(sent, "x-gateway-token"));
        // The adapter's own headers must survive the addition.
        Assert.Equal("test-key", Header(sent, "x-api-key"));
        Assert.NotNull(Header(sent, "anthropic-version"));
    }

    // A provider with no headers configured must not start sending anything of its own.
    [Fact]
    public async Task No_configured_headers_means_no_extra_headers()
    {
        var sent = await CaptureAsync(
            http => new OpenAiCompatibleProvider(http, Descriptor(ProviderKind.OpenAiCompatible)),
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");

        Assert.Null(Header(sent, "x-gateway-token"));
        Assert.Equal("Bearer test-key", Header(sent, "Authorization"));
    }

    // ---- the token budget -----------------------------------------------------------

    [Fact]
    public async Task OpenAiCompatible_sends_the_descriptors_max_tokens()
    {
        var body = await CaptureBodyAsync(
            http => new OpenAiCompatibleProvider(
                http, Descriptor(ProviderKind.OpenAiCompatible, maxTokens: 4096)),
            Request());

        Assert.Equal(4096, MaxTokensIn(body));
    }

    [Fact]
    public async Task The_requests_max_tokens_wins_over_the_descriptors()
    {
        var body = await CaptureBodyAsync(
            http => new OpenAiCompatibleProvider(
                http, Descriptor(ProviderKind.OpenAiCompatible, maxTokens: 4096)),
            Request(maxTokens: 256));

        Assert.Equal(256, MaxTokensIn(body));
    }

    // Nothing configured means nothing sent — the endpoint's own default must keep applying rather
    // than being replaced by a number this app invented.
    [Fact]
    public async Task Nothing_configured_means_no_max_tokens_field()
    {
        var body = await CaptureBodyAsync(
            http => new OpenAiCompatibleProvider(http, Descriptor(ProviderKind.OpenAiCompatible)),
            Request());

        Assert.Null(MaxTokensIn(body));
    }

    // ---- plumbing --------------------------------------------------------------------

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static int? MaxTokensIn(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("max_tokens", out var value) ? value.GetInt32() : null;
    }

    private static async Task<HttpRequestMessage> CaptureAsync(
        Func<HttpClient, IChatProvider> build, string responseJson)
    {
        var handler = new CapturingHandler(responseJson);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test") };

        await build(http).CompleteAsync(Request(), CancellationToken.None);
        return handler.LastRequest!;
    }

    private static async Task<string> CaptureBodyAsync(
        Func<HttpClient, IChatProvider> build, ChatRequest request)
    {
        var handler = new CapturingHandler(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test") };

        await build(http).CompleteAsync(request, CancellationToken.None);
        return handler.LastBody!;
    }

    /// <summary>Answers with a canned body and keeps the request it was given.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public CapturingHandler(string body) => _body = body;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
