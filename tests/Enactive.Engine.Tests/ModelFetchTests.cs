namespace Enactive.Engine.Tests;

using System.Net;
using System.Net.Http;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// Asking a provider which models it has.
///
/// <para>The Refresh button asked one question — "is this Anthropic?" — and sent everything else to
/// Ollama's <c>/api/tags</c>. So an OpenAI-compatible endpoint (LM Studio, vLLM, OpenRouter, any
/// gateway) answered 404 and the model list came back empty, on a provider kind the rest of the app
/// supports end to end.</para>
/// </summary>
public sealed class ModelFetchTests
{
    /// <summary>Answers every request with one canned response, and keeps what it was asked.</summary>
    private sealed class Canned : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public Canned(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private const string OpenAiCatalogue = """
        {"object":"list","data":[{"id":"qwen2.5-coder-32b","object":"model"},{"id":"llama-3.3-70b"}]}
        """;

    // ── the endpoint that had no catalogue at all ─────────────────────────────────────

    [Fact]
    public async Task An_openai_compatible_endpoint_is_asked_its_own_catalogue()
    {
        var handler = new Canned(OpenAiCatalogue);
        using var http = new HttpClient(handler);

        var models = await ModelFetch.OpenAiCompatibleAsync(http, "http://localhost:1234/v1", "sk-local");

        Assert.Equal(new[] { "qwen2.5-coder-32b", "llama-3.3-70b" }, models);
        Assert.Equal("http://localhost:1234/v1/models", handler.Seen!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-local", handler.Seen.Headers.GetValues("Authorization").Single());
    }

    // A local server usually wants no key, and sending an empty Bearer is worse than sending none.
    [Fact]
    public async Task No_key_means_no_authorization_header()
    {
        var handler = new Canned(OpenAiCatalogue);
        using var http = new HttpClient(handler);

        await ModelFetch.OpenAiCompatibleAsync(http, "http://localhost:1234/v1", "");

        Assert.False(handler.Seen!.Headers.Contains("Authorization"));
    }

    // An endpoint that needs a header to CHAT needs it to LIST; a gateway keyed by an organisation
    // header would otherwise refuse only here, which reads as "this provider has no models".
    [Fact]
    public async Task The_providers_own_headers_are_sent_too()
    {
        var handler = new Canned(OpenAiCatalogue);
        using var http = new HttpClient(handler);

        await ModelFetch.OpenAiCompatibleAsync(
            http, "https://gateway.example/v1", "sk-x",
            new Dictionary<string, string> { ["X-Org"] = "acme" });

        Assert.Equal("acme", handler.Seen!.Headers.GetValues("X-Org").Single());
    }

    [Fact]
    public async Task A_trailing_slash_does_not_become_a_double_slash()
    {
        var handler = new Canned(OpenAiCatalogue);
        using var http = new HttpClient(handler);

        await ModelFetch.OpenAiCompatibleAsync(http, "http://localhost:1234/v1/", null);

        Assert.Equal("http://localhost:1234/v1/models", handler.Seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_provider_with_no_base_url_is_told_so_rather_than_asked()
    {
        using var http = new HttpClient(new Canned(OpenAiCatalogue));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ModelFetch.OpenAiCompatibleAsync(http, "   ", "sk-x"));
    }

    [Fact]
    public async Task A_refusal_is_an_error_not_an_empty_list()
    {
        using var http = new HttpClient(new Canned("nope", HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => ModelFetch.OpenAiCompatibleAsync(http, "https://gateway.example/v1", "wrong"));
    }

    // ── and the two that already worked still do ──────────────────────────────────────

    [Fact]
    public async Task Ollama_is_asked_api_tags_with_the_v1_suffix_taken_off()
    {
        var handler = new Canned("""{"models":[{"name":"gemma3:12b"},{"name":"qwen3:14b"}]}""");
        using var http = new HttpClient(handler);

        var models = await ModelFetch.OllamaAsync(http, "http://localhost:11434/v1");

        Assert.Equal(new[] { "gemma3:12b", "qwen3:14b" }, models);
        Assert.Equal("http://localhost:11434/api/tags", handler.Seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Anthropic_is_asked_with_its_own_headers()
    {
        var handler = new Canned("""{"data":[{"id":"claude-sonnet-4-6"}]}""");
        using var http = new HttpClient(handler);

        var models = await ModelFetch.AnthropicAsync(http, "sk-ant-x", workspaceId: "ws-1");

        Assert.Equal(new[] { "claude-sonnet-4-6" }, models);
        Assert.Equal("sk-ant-x", handler.Seen!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("ws-1", handler.Seen.Headers.GetValues("anthropic-workspace-id").Single());
    }

    // ── which catalogue gets asked ────────────────────────────────────────────────────

    // The bug itself. Every kind that is not Anthropic and not Ollama is OpenAI-compatible, and must
    // be asked its own /models — not Ollama's /api/tags, which it answers with a 404.
    [Fact]
    public async Task An_openai_compatible_provider_is_not_sent_to_ollamas_catalogue()
    {
        var handler = new Canned(OpenAiCatalogue);
        using var http = new HttpClient(handler);

        var models = await ModelFetch.ForAsync(
            http, ProviderKind.OpenAiCompatible, "http://localhost:1234/v1", "sk-local");

        Assert.Equal("http://localhost:1234/v1/models", handler.Seen!.RequestUri!.ToString());
        Assert.NotEmpty(models);
    }

    [Fact]
    public async Task An_ollama_provider_still_goes_to_api_tags()
    {
        var handler = new Canned("""{"models":[{"name":"gemma3:12b"}]}""");
        using var http = new HttpClient(handler);

        await ModelFetch.ForAsync(http, ProviderKind.OllamaNative, "http://localhost:11434", null);

        Assert.Equal("http://localhost:11434/api/tags", handler.Seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task An_anthropic_provider_still_goes_to_anthropic()
    {
        var handler = new Canned("""{"data":[{"id":"claude-sonnet-4-6"}]}""");
        using var http = new HttpClient(handler);

        await ModelFetch.ForAsync(
            http, ProviderKind.Anthropic, "https://api.anthropic.com", "sk-ant-x",
            new Dictionary<string, string> { ["anthropic-workspace-id"] = "ws-1" });

        Assert.StartsWith("https://api.anthropic.com/v1/models", handler.Seen!.RequestUri!.ToString());
        Assert.Equal("ws-1", handler.Seen.Headers.GetValues("anthropic-workspace-id").Single());
    }

    // ── the shape both catalogues share ───────────────────────────────────────────────

    [Fact]
    public async Task An_answer_in_an_unexpected_shape_lists_nothing_rather_than_guessing()
    {
        using var http = new HttpClient(new Canned("""{"models":["not the openai shape"]}"""));

        Assert.Empty(await ModelFetch.OpenAiCompatibleAsync(http, "https://x/v1", null));
    }
}
