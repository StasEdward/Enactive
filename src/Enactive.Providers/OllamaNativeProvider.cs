namespace Enactive.Providers;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;
using Enactive.Core.Tools;

/// <summary>
/// Talks to Ollama's own /api/chat (not the OpenAI-compatible /v1 shim). The only reason this
/// exists alongside <see cref="OpenAiCompatibleProvider"/>: the /v1 endpoint silently ignores any
/// context-window override (confirmed against Ollama's openai/openai.go - ChatCompletionRequest has
/// no "options"/"num_ctx" field at all), so a per-run num_ctx only takes effect through this native
/// wire format. Streaming here is newline-delimited JSON objects, not SSE.
/// </summary>
public sealed class OllamaNativeProvider : IChatProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly HttpClient _http;
    private readonly ProviderDescriptor _descriptor;
    private readonly ILogSink? _log;

    public OllamaNativeProvider(HttpClient http, ProviderDescriptor descriptor, ILogSink? log = null)
    {
        _http = http;
        _descriptor = descriptor;
        _log = log;
    }

    /// <summary>
    /// num_ctx IS the window: Ollama gives prompt and generation one shared budget, and a prompt
    /// that fills it leaves the model no room to answer. Null when the request sets none — the
    /// model then runs with whatever it was loaded with, which this side cannot see.
    /// </summary>
    public int? ContextWindow(ChatRequest request) => request.NumCtx;

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var httpRequest = BuildHttpRequest(request, stream: true);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            WireTap.Error(_log, _descriptor.Id, (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Provider '{_descriptor.Id}' returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(errorBody, 500)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var raw = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                raw.AppendLine(line);
                if (line.Length == 0)
                    continue;

                foreach (var evt in ParseStreamLine(line))
                    yield return evt;
            }
        }
        finally
        {
            WireTap.Response(_log, _descriptor.Id, (int)response.StatusCode, raw.ToString(), streamed: true);
        }
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        using var httpRequest = BuildHttpRequest(request, stream: false);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            WireTap.Error(_log, _descriptor.Id, (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Provider '{_descriptor.Id}' returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 500)}");
        }

        WireTap.Response(_log, _descriptor.Id, (int)response.StatusCode, body);
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
        if (request.Temperature is { } temperature || request.NumCtx is { } numCtx0)
        {
            var options = new Dictionary<string, object?>();
            if (request.Temperature is { } t) options["temperature"] = t;
            if (request.NumCtx is { } nc) options["num_ctx"] = nc;
            payload["options"] = options;
        }
        if (request.Think is { } think)
            payload["think"] = think;
        if (request.Tools is { Count: > 0 } tools)
            payload["tools"] = tools.Select(ToWireTool).ToArray();

        var url = RootUrl(_descriptor.BaseUrl) + "/api/chat";
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        WireTap.Request(_log, _descriptor.Id, request.Model, json);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_descriptor.ApiKey))
            httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _descriptor.ApiKey);

        // Ollama itself rarely needs these, but a remote one behind a reverse proxy or a tunnel does,
        // and the provider editor offers the field either way.
        ProviderHeaders.Apply(httpRequest, _descriptor);
        return httpRequest;
    }

    /// <summary>Ollama's native API lives at the server root, not under "/v1" - strip it if present
    /// so the same "http://localhost:11434/v1" endpoint string works for both providers.</summary>
    private static string RootUrl(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3].TrimEnd('/');
        return root;
    }

    private static IEnumerable<ChatStreamEvent> ParseStreamLine(string line)
    {
        var events = new List<ChatStreamEvent>();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } text)
            {
                events.Add(new TextDelta(text));
            }

            // Ollama returns a reasoning model's deliberation in its own field. Reading only
            // "content" made such a turn arrive completely empty - see ReasoningDelta.
            if (message.TryGetProperty("thinking", out var thinking)
                && thinking.ValueKind == JsonValueKind.String
                && thinking.GetString() is { Length: > 0 } reasoning)
            {
                events.Add(new ReasoningDelta(reasoning));
            }

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                // Ollama emits each tool call fully formed in one chunk (no incremental argument
                // streaming like OpenAI's format), so this fires once per call with the complete
                // arguments already - ToolCallDelta just happens to also support partial chunks.
                var index = 0;
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var (name, argsJson) = ReadFunction(tc);
                    events.Add(new ToolCallDelta(index, Guid.NewGuid().ToString("N"), name, argsJson));
                    index++;
                }
            }
        }

        if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
        {
            int? prompt = root.TryGetProperty("prompt_eval_count", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : null;
            int? completion = root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : null;
            if (prompt is not null || completion is not null)
                events.Add(new UsageDelta(prompt, completion));

            var reason = root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String
                ? dr.GetString()
                : "stop";
            events.Add(new FinishDelta(reason));
        }

        return events;
    }

    private static (string? Name, string ArgsJson) ReadFunction(JsonElement toolCall)
    {
        if (!toolCall.TryGetProperty("function", out var fn))
            return (null, "{}");

        string? name = fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;

        // Ollama's native wire sends arguments as a JSON *object*, unlike OpenAI's stringified form -
        // re-serialize it to the string shape the rest of Enactive (ToolCall.ArgumentsJson) expects.
        var argsJson = fn.TryGetProperty("arguments", out var argsEl)
            ? argsEl.GetRawText()
            : "{}";

        return (name, argsJson);
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
            return new { role = "tool", content = m.Content ?? "" };

        if (m.ToolCalls is { Count: > 0 } calls)
        {
            return new
            {
                role = "assistant",
                content = m.Content ?? "",
                tool_calls = calls.Select(tc => new
                {
                    function = new
                    {
                        name = tc.Name,
                        // Native Ollama wants the arguments as an object, not a JSON string.
                        arguments = JsonDocument.Parse(
                            string.IsNullOrWhiteSpace(tc.ArgumentsJson) ? "{}" : tc.ArgumentsJson).RootElement.Clone()
                    }
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

        if (!root.TryGetProperty("message", out var message))
            throw new InvalidOperationException("Provider response contained no message.");

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
                var (name, argsJson) = ReadFunction(tc);
                toolCalls.Add(new ToolCall(Guid.NewGuid().ToString("N"), name ?? "", argsJson));
            }
        }

        var finishReason = root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String
            ? dr.GetString()
            : "stop";

        int? promptTokens = root.TryGetProperty("prompt_eval_count", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : null;
        int? completionTokens = root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : null;

        var thinking = message.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String
            ? th.GetString()
            : null;

        var assistant = new ChatMessage(ChatRole.Assistant, content, toolCalls);
        return new ChatCompletion(assistant, finishReason, promptTokens, completionTokens, thinking);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
