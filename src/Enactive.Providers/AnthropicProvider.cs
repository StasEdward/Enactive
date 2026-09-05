namespace Enactive.Providers;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;
using Enactive.Core.Tools;

/// <summary>
/// Anthropic Messages API adapter. Used as the reasoning/review agent in multi-agent mode.
/// Non-streaming CompleteAsync (planning + review need only that); StreamChatAsync wraps it as one chunk.
/// </summary>
public sealed class AnthropicProvider : IChatProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 32000;
    private static readonly JsonSerializerOptions JsonOpts = new();

    // Discovered per-model output caps: on a 400 saying our max_tokens exceeds the model's limit, we parse
    // the real maximum from the error and remember it, so later calls to that model request the right size.
    private static readonly ConcurrentDictionary<string, int> ModelCaps = new();

    private readonly HttpClient _http;
    private readonly ProviderDescriptor _descriptor;
    private readonly ILogSink? _log;

    public AnthropicProvider(HttpClient http, ProviderDescriptor descriptor, ILogSink? log = null)
    {
        _http = http;
        _descriptor = descriptor;
        _log = log;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var completion = await CompleteAsync(request, ct);
        if (completion.Message.Content is { Length: > 0 } text)
            yield return new TextDelta(text);
        if (completion.Message.ToolCalls is { Count: > 0 } calls)
            foreach (var call in calls)
                yield return new ToolCallDelta(0, call.Id, call.Name, call.ArgumentsJson);

        // The counts were already in the response and were being thrown away here, which is why a
        // run on Claude reported no tokens at all while a local one did.
        if (completion.PromptTokens is not null || completion.CompletionTokens is not null)
            yield return new UsageDelta(completion.PromptTokens, completion.CompletionTokens);

        yield return new FinishDelta(completion.FinishReason);
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var systemParts = new List<string>();
        var wire = new List<object>();

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.System:
                    if (!string.IsNullOrEmpty(m.Content))
                        systemParts.Add(m.Content);
                    break;

                case ChatRole.Tool:
                    wire.Add(new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "tool_result", tool_use_id = m.ToolCallId ?? "", content = m.Content ?? "" }
                        }
                    });
                    break;

                case ChatRole.Assistant when m.ToolCalls is { Count: > 0 } calls:
                    var blocks = new List<object>();
                    if (!string.IsNullOrEmpty(m.Content))
                        blocks.Add(new { type = "text", text = m.Content });
                    foreach (var call in calls)
                        blocks.Add(new { type = "tool_use", id = call.Id, name = call.Name, input = ToElement(call.ArgumentsJson) });
                    wire.Add(new { role = "assistant", content = blocks.ToArray() });
                    break;

                default:
                    wire.Add(new
                    {
                        role = m.Role == ChatRole.Assistant ? "assistant" : "user",
                        content = new object[] { new { type = "text", text = m.Content ?? "" } }
                    });
                    break;
            }
        }

        // Build + send in a local function so we can retry once without `temperature`: newer Anthropic models
        // (e.g. Opus 5.x) reject it with 400 "temperature is deprecated for this model", while older ones still
        // accept it — so we keep it by default and only drop it when the API tells us this model refuses it.
        async Task<(bool Ok, int Status, string Body)> SendAsync(bool includeTemperature, int maxTokens)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["max_tokens"] = maxTokens,
                ["messages"] = wire.ToArray()
            };
            if (systemParts.Count > 0)
                payload["system"] = string.Join("\n", systemParts);
            if (includeTemperature && request.Temperature is { } temperature)
                payload["temperature"] = temperature;
            if (request.Tools is { Count: > 0 } tools)
                payload["tools"] = tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    input_schema = ToElement(t.JsonSchema)
                }).ToArray();

            var url = _descriptor.BaseUrl.TrimEnd('/') + "/v1/messages";
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            WireTap.Request(_log, _descriptor.Id, request.Model, json);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", _descriptor.ApiKey ?? "");
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            if (_descriptor.Headers is { } extraHeaders)
                foreach (var header in extraHeaders)
                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, responseBody);
        }

        // Output budget: an explicit per-provider override wins; else a cap already discovered for this
        // model; else a generous default big enough for large documents (self-corrects downward below).
        var includeTemperature = true;
        var maxTokens = _descriptor.MaxTokens
            ?? request.MaxTokens
            ?? (ModelCaps.TryGetValue(request.Model, out var known) ? known : DefaultMaxTokens);

        var (ok, status, body) = await SendAsync(includeTemperature, maxTokens);

        // Recover from the two 400s Anthropic returns for otherwise-valid requests: `temperature` is
        // deprecated on newer models, and max_tokens above the model's cap (the error names the cap, which
        // we parse and remember). Bounded to two retries so we can fix at most both.
        for (var attempt = 0; attempt < 2 && !ok && status == 400; attempt++)
        {
            if (includeTemperature && request.Temperature is not null && IsTemperatureDeprecated(body))
            {
                includeTemperature = false;
            }
            else if (TryParseMaxTokensCap(body, maxTokens, out var cap))
            {
                ModelCaps[request.Model] = cap;
                maxTokens = cap;
            }
            else
            {
                break;
            }
            (ok, status, body) = await SendAsync(includeTemperature, maxTokens);
        }

        if (!ok)
        {
            WireTap.Error(_log, _descriptor.Id, status, body);
            throw new HttpRequestException($"Anthropic returned {status}: {Truncate(body, 500)}");
        }

        WireTap.Response(_log, _descriptor.Id, status, body);
        return ParseCompletion(body);
    }

    private static bool IsTemperatureDeprecated(string body)
        => body.Contains("temperature", StringComparison.OrdinalIgnoreCase)
           && body.Contains("deprecated", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detects the "max_tokens above the model's cap" 400 and extracts the allowed maximum. The message
    /// reads like "... 32000 &gt; 64000, which is the maximum ..." — the number after '&gt;' is the cap.
    /// Falls back to a universally safe 8192 when the number can't be parsed.
    /// </summary>
    private static bool TryParseMaxTokensCap(string body, int requested, out int cap)
    {
        cap = 0;
        if (!body.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            return false;
        var m = Regex.Match(body, @">\s*(\d+)");
        cap = m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 8192;
        return cap > 0 && cap < requested;
    }

    private static ChatCompletion ParseCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var text = new StringBuilder();
        List<ToolCall>? toolCalls = null;

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                {
                    text.Append(txt.GetString());
                }
                else if (type == "tool_use")
                {
                    toolCalls ??= new List<ToolCall>();
                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    toolCalls.Add(new ToolCall(id, name, input));
                }
            }
        }

        var finish = root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;

        int? inputTokens = null, outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) inputTokens = itv;
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) outputTokens = otv;
        }

        var contentText = text.Length > 0 ? text.ToString() : null;
        return new ChatCompletion(new ChatMessage(ChatRole.Assistant, contentText, toolCalls), finish, inputTokens, outputTokens);
    }

    private static JsonElement ToElement(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
