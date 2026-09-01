namespace AIClient.Providers;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIClient.Core.Chat;
using AIClient.Core.Providers;
using AIClient.Core.Tools;

/// <summary>
/// One adapter for every OpenAI-compatible endpoint: OpenAI, Ollama (/v1), OpenRouter, Groq,
/// LM Studio, vLLM, ... Only the base URL / key / model change. Streaming uses a small hand-rolled
/// SSE reader (no external packages).
/// </summary>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    // Default (no naming policy) keeps wire field names like "tool_calls" verbatim.
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly HttpClient _http;
    private readonly ProviderDescriptor _descriptor;

    public OpenAiCompatibleProvider(HttpClient http, ProviderDescriptor descriptor)
    {
        _http = http;
        _descriptor = descriptor;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var httpRequest = BuildHttpRequest(request, stream: true);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Provider '{_descriptor.Id}' returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(errorBody, 500)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
                yield break;

            foreach (var evt in ParseStreamChunk(data))
                yield return evt;
        }
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        using var httpRequest = BuildHttpRequest(request, stream: false);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Provider '{_descriptor.Id}' returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 500)}");
        }

        return ParseCompletion(body);
    }

    private HttpRequestMessage BuildHttpRequest(ChatRequest request, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["stream"] = stream,
            ["messages"] = request.Messages.Select(ToWire).ToArray()
        };
        if (request.Temperature is { } temperature)
            payload["temperature"] = temperature;
        if (request.Tools is { Count: > 0 } tools)
            payload["tools"] = tools.Select(ToWireTool).ToArray();

        var url = _descriptor.BaseUrl.TrimEnd('/') + "/chat/completions";
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_descriptor.ApiKey))
            httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _descriptor.ApiKey);
        return httpRequest;
    }

    private static IEnumerable<ChatStreamEvent> ParseStreamChunk(string data)
    {
        var events = new List<ChatStreamEvent>();
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];

            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } text)
                {
                    events.Add(new TextDelta(text));
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0;
                        var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() : null;

                        string? name = null;
                        string? argumentsPart = null;
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                name = nameEl.GetString();
                            if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                                argumentsPart = argsEl.GetString();
                        }

                        events.Add(new ToolCallDelta(index, id, name, argumentsPart));
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                events.Add(new FinishDelta(fr.GetString()));
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            int? prompt = usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : null;
            int? completion = usage.TryGetProperty("completion_tokens", out var cpt) && cpt.TryGetInt32(out var cptv) ? cptv : null;
            if (prompt is not null || completion is not null)
                events.Add(new UsageDelta(prompt, completion));
        }

        return events;
    }

    private static object ToWireTool(ToolDefinition t) => new
    {
        type = "function",
        function = new
        {
            name = t.Name,
            description = t.Description,
            parameters = JsonDocument.Parse(t.JsonSchema).RootElement.Clone()
        }
    };

    private static object ToWire(ChatMessage m)
    {
        if (m.Role == ChatRole.Tool)
            return new { role = "tool", tool_call_id = m.ToolCallId ?? "", content = m.Content ?? "" };

        if (m.ToolCalls is { Count: > 0 } calls)
        {
            return new
            {
                role = "assistant",
                content = m.Content ?? "",
                tool_calls = calls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = tc.ArgumentsJson }
                }).ToArray()
            };
        }

        return new { role = RoleString(m.Role), content = m.Content ?? "" };
    }

    private static string RoleString(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user"
    };

    private static ChatCompletion ParseCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Provider response contained no choices.");

        var message = choices[0].GetProperty("message");

        string? content = null;
        if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            content = contentEl.GetString();

        List<ToolCall>? toolCalls = null;
        if (message.TryGetProperty("tool_calls", out var toolCallsEl)
            && toolCallsEl.ValueKind == JsonValueKind.Array && toolCallsEl.GetArrayLength() > 0)
        {
            toolCalls = new List<ToolCall>();
            foreach (var tc in toolCallsEl.EnumerateArray())
            {
                var function = tc.GetProperty("function");
                var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()!
                    : Guid.NewGuid().ToString("N");
                var name = function.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";

                var arguments = "{}";
                if (function.TryGetProperty("arguments", out var argsEl))
                {
                    arguments = argsEl.ValueKind == JsonValueKind.String
                        ? (argsEl.GetString() ?? "{}")
                        : argsEl.GetRawText();
                }

                toolCalls.Add(new ToolCall(id, name, arguments));
            }
        }

        string? finishReason = null;
        if (choices[0].TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
            finishReason = frEl.GetString();

        int? promptTokens = null, completionTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv)) promptTokens = ptv;
            if (usage.TryGetProperty("completion_tokens", out var cpt) && cpt.TryGetInt32(out var cptv)) completionTokens = cptv;
        }

        var assistant = new ChatMessage(ChatRole.Assistant, content, toolCalls);
        return new ChatCompletion(assistant, finishReason, promptTokens, completionTokens);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
