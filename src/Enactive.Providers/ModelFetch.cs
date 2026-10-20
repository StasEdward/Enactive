using System.Text.Json;
using Enactive.Core.Providers;

namespace Enactive.Providers;

/// <summary>
/// Lists the models a provider exposes, for the "Refresh" buttons in the settings editors.
///
/// <para>Here rather than in the window: which catalogue a provider has is a fact about the
/// provider, and keeping it beside the adapters is what makes it testable at all.</para>
/// </summary>
public static class ModelFetch
{
    /// <summary>
    /// The catalogue this provider actually has.
    ///
    /// <para>The choice lives here, not in the settings window. It used to be a conditional in the
    /// view model that asked "is this Anthropic?" and sent everything else to Ollama's
    /// <c>/api/tags</c> — so every OpenAI-compatible endpoint answered 404 and its model list came
    /// back empty. A fact about a provider belongs beside the provider, where it can be checked.</para>
    /// </summary>
    public static Task<List<string>> ForAsync(
        HttpClient http, ProviderKind kind, string baseUrl, string? apiKey,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => kind switch
        {
            ProviderKind.Anthropic => AnthropicAsync(
                http, apiKey ?? string.Empty,
                headers is not null && headers.TryGetValue("anthropic-workspace-id", out var workspace)
                    ? workspace
                    : null, baseUrl, headers, ct),

            ProviderKind.OllamaNative => OllamaAsync(http, baseUrl, apiKey, headers, ct),

            _ => OpenAiCompatibleAsync(http, baseUrl, apiKey, headers, ct)
        };

    /// <summary>Ollama's installed models via /api/tags (accepts an OpenAI-style base URL ending in /v1).</summary>
    public static async Task<List<string>> OllamaAsync(HttpClient http, string baseUrl,
        string? apiKey = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3].TrimEnd('/');
        var url = root + "/api/tags";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        ProviderHeaders.Apply(request, headers);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var model in models.EnumerateArray())
                if (model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    list.Add(name.GetString()!);
        return list;
    }

    /// <summary>Anthropic's model catalog via /v1/models.</summary>
    public static async Task<List<string>> AnthropicAsync(HttpClient http, string apiKey, string? workspaceId,
        string baseUrl = "https://api.anthropic.com", IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        using var request = AnthropicRequest("/models?limit=1000", apiKey, workspaceId, baseUrl, headers);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        return IdsFrom(json);
    }

    /// <summary>A GET to Anthropic's catalog, with the key, the version and the provider's own headers - one path for the
    /// list and for one model's entry, so a header a provider needs reaches both.</summary>
    private static HttpRequestMessage AnthropicRequest(string path, string? apiKey, string? workspaceId, string baseUrl,
        IReadOnlyDictionary<string, string>? headers)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (!root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1";
        var request = new HttpRequestMessage(HttpMethod.Get, root + path);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!string.IsNullOrWhiteSpace(workspaceId))
            request.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);

        ProviderHeaders.Apply(request, headers);
        return request;
    }

    // ── the window a server states for one model (ServerWindow) ──────────────────────────────

    /// <summary>
    /// The prompt size Anthropic accepts for <paramref name="model"/>: <c>max_input_tokens</c> of
    /// <c>GET /v1/models/{model}</c>, or null when the entry does not say.
    ///
    /// <para>The right number because it is the API's own limit on the input of a request to that model, not a figure from
    /// a page about it: a prompt held under it is not refused for its size. As the engine's window it errs the safe way -
    /// the engine keeps prompt and answer together under it, so the answer's room comes out of the input's limit. Asked
    /// with the provider's own headers, so a header that changes what the model accepts (a beta for a larger window)
    /// changes what the catalog is asked about too.</para>
    /// </summary>
    /// <exception cref="HttpRequestException">The server could not answer now (unreachable, 5xx, 408, 429, 401, 403): ask again later.</exception>
    public static async Task<int?> AnthropicWindowAsync(HttpClient http, ProviderDescriptor provider, string model,
        CancellationToken ct = default)
    {
        var workspace = provider.Headers is not null && provider.Headers.TryGetValue("anthropic-workspace-id", out var w) ? w : null;
        using var request = AnthropicRequest("/models/" + Uri.EscapeDataString(model), provider.ApiKey, workspace,
            provider.BaseUrl, provider.Headers);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!Answered(response))
            return null;
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Number(json, "max_input_tokens");
    }

    /// <summary>
    /// The context ONE request gets on a llama.cpp server: <c>GET /props</c>, its
    /// <c>default_generation_settings.n_ctx</c>. Null from any server that has no such endpoint or does not say - every
    /// other OpenAI-compatible server.
    ///
    /// <para>Per slot, not the model's: llama.cpp splits the context it was started with (<c>-c</c>) between its
    /// <c>--parallel</c> slots, and a request runs in one slot. The slot's <c>n_ctx</c> is what <c>/props</c> reports in
    /// its default generation settings - with <c>-c 32768 --parallel 4</c>, 8192 (the whole 32,768 where the server
    /// shares one cache between its slots, <c>--kv-unified</c>, and says so). The model's trained context
    /// (<c>n_ctx_train</c>, in <c>/v1/models</c>) is not what a request gets, and a window larger than the real one costs a
    /// request cut off mid-answer. A server that reports only a total <c>n_ctx</c> beside its <c>total_slots</c> is read as
    /// the same split: the smaller reading, because a wrong number in that direction costs some room, never a request.</para>
    ///
    /// <para>Asked with <c>?model=</c>, because a llama.cpp server that hosts several models answers <c>/props</c> for
    /// the one named; a server with one model ignores it.</para>
    ///
    /// <para>No other OpenAI-compatible field is used. vLLM's <c>max_model_len</c> and LM Studio's and OpenRouter's
    /// context lengths are each one server's own field, not part of the protocol, and Ollama's /v1 runs a request at its
    /// own default context whatever the model could take.</para>
    /// </summary>
    /// <exception cref="HttpRequestException">The server could not answer now (unreachable, 5xx, 408, 429, 401, 403): ask again later.</exception>
    public static async Task<int?> LlamaCppWindowAsync(HttpClient http, ProviderDescriptor provider, string model,
        CancellationToken ct = default)
    {
        var root = provider.BaseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3].TrimEnd('/');
        if (root.Length == 0)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/props?model=" + Uri.EscapeDataString(model));
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + provider.ApiKey);
        ProviderHeaders.Apply(request, provider.Headers);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!Answered(response))
            return null;
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var props = doc.RootElement;
            if (props.ValueKind != JsonValueKind.Object)
                return null;
            if (props.TryGetProperty("default_generation_settings", out var settings)
                && settings.ValueKind == JsonValueKind.Object
                && Positive(settings, "n_ctx") is { } slot)
                return slot;
            if (Positive(props, "n_ctx") is { } total)
                return Positive(props, "total_slots") is > 1 and var slots ? total / slots : total;
            return null;
        }
        catch (JsonException)
        {
            // A gateway that answers every path with its own page: it said nothing about a window.
            return null;
        }
    }

    /// <summary>
    /// Whether the server gave an answer to read. A refusal it will repeat (404, 400) is an answer - "nothing here" - and is
    /// not asked again; one that may differ in a minute throws, so the question is asked again later: 5xx, 408 and 429, and
    /// 401 and 403 too - a key that is wrong now is put right in the settings without a restart, and read as "nothing
    /// here" it would leave the window unknown for the life of the process.
    /// </summary>
    private static bool Answered(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status >= 500 || status is 401 or 403 or 408 or 429)
            throw new HttpRequestException($"The server answered {status} {response.StatusCode}.", null, response.StatusCode);
        return response.IsSuccessStatusCode;
    }

    private static int? Number(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Positive(doc.RootElement, field) : null;
        }
        catch (JsonException) { return null; }
    }

    private static int? Positive(JsonElement element, string field)
        => element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var n) && n > 0
            ? (int)Math.Min(n, int.MaxValue)
            : null;

    /// <summary>
    /// An OpenAI-compatible catalogue: <c>GET {baseUrl}/models</c>, answered as
    /// <c>{"data":[{"id":"..."}]}</c>.
    ///
    /// <para>This did not exist. The Refresh button asked "is this Anthropic?" and sent everything
    /// else to Ollama's <c>/api/tags</c> — so LM Studio, vLLM, OpenRouter and every other
    /// OpenAI-compatible endpoint answered 404 and the model list came back empty, on a provider
    /// kind the app otherwise supports end to end.</para>
    /// </summary>
    public static async Task<List<string>> OpenAiCompatibleAsync(
        HttpClient http, string baseUrl, string? apiKey, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (root.Length == 0)
            throw new InvalidOperationException("This provider has no base URL to ask.");

        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/models");

        // Bearer is what an OpenAI-compatible endpoint expects. A local server usually wants no key
        // at all, and sending an empty one is worse than sending none.
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

        // The provider's own headers, because an endpoint that needs one to CHAT needs it to list
        // too — a gateway keyed by an organisation header would otherwise refuse only here.
        ProviderHeaders.Apply(request, headers);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return IdsFrom(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The <c>data[].id</c> shape, shared by Anthropic and every OpenAI-compatible server.</summary>
    private static List<string> IdsFrom(string json)
    {
        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
                if (model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } name)
                    list.Add(name);
        }
        return list;
    }

    /// <summary>"key: value" per line, ignoring blanks and lines without a colon.</summary>
    public static Dictionary<string, string> ParseHeaders(string? text)
    {
        var headers = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(text))
            return headers;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var idx = line.IndexOf(':');
            if (idx <= 0)
                continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Length > 0)
                headers[key] = value;
        }
        return headers;
    }

    /// <summary>Headers as "key: value" lines, for the editor text box.</summary>
    public static string FormatHeaders(IReadOnlyDictionary<string, string> headers)
        => string.Join("\n", headers.Select(kv => $"{kv.Key}: {kv.Value}"));
}
