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
        var root = baseUrl.Trim().TrimEnd('/');
        if (!root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1";
        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/models?limit=1000");
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!string.IsNullOrWhiteSpace(workspaceId))
            request.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);

        ProviderHeaders.Apply(request, headers);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        return IdsFrom(json);
    }

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
