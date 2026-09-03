using System.Text.Json;

namespace AIClient.App.Ui;

/// <summary>Lists the models a provider exposes, for the "Refresh" buttons in the settings editors.</summary>
internal static class ModelFetch
{
    /// <summary>Ollama's installed models via /api/tags (accepts an OpenAI-style base URL ending in /v1).</summary>
    public static async Task<List<string>> OllamaAsync(HttpClient http, string baseUrl)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3].TrimEnd('/');
        var url = root + "/api/tags";

        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var model in models.EnumerateArray())
                if (model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    list.Add(name.GetString()!);
        return list;
    }

    /// <summary>Anthropic's model catalog via /v1/models.</summary>
    public static async Task<List<string>> AnthropicAsync(HttpClient http, string apiKey, string? workspaceId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1000");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!string.IsNullOrWhiteSpace(workspaceId))
            request.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var model in data.EnumerateArray())
                if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    list.Add(id.GetString()!);
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
