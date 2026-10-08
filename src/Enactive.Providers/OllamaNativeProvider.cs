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
    /// that fills it leaves the model no room to answer. Null when neither request nor descriptor sets it — the
    /// model then runs with whatever it was loaded with, which this side cannot see.
    /// </summary>
    public int? ContextWindow(ChatRequest request) => request.NumCtx ?? _descriptor.ContextWindowTokens;

    public int? AnswerReserve(ChatRequest request) => _descriptor.AnswerReserveTokens;

    public int? HandoverAtPercent(ChatRequest request) => _descriptor.HandoverAtPercent;
    public int? WorkingContext(ChatRequest request) => _descriptor.WorkingContextTokens;
    public int ReasoningAllowance(ChatRequest request) => Math.Clamp(_descriptor.ReasoningTokenAllowance ?? (request.Think == true ? 8192 : 0), 0, 65536);

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var httpRequest = BuildHttpRequest(request, stream: true);
        using var response = await ProviderDeadline.HeadersAsync(_http, httpRequest, _descriptor.StreamIdleTimeoutSeconds, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ProviderHttpError.ReadBodyAsync(response, _descriptor.StreamIdleTimeoutSeconds, ct);
            WireTap.Error(_log, _descriptor.Id, (int)response.StatusCode, errorBody);
            throw ProviderHttpError.Create(
                $"Provider '{_descriptor.Id}' returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(errorBody, 500)}", (int)response.StatusCode, ProviderHttpError.RetryAfter(response));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(new IdleTimeoutStream(stream, TimeSpan.FromSeconds(Math.Clamp(_descriptor.StreamIdleTimeoutSeconds, 1, 86400))));
        var calls = new StreamCallIdentity();
        var raw = BoundedLogBuffer.Create(_log, LogLevel.Trace);
        var finished = false;
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (raw is not null && !_log.IsLoggingEnabled(LogLevel.Trace)) raw = null;
                raw?.AppendLine(line);
                ProviderResponse.RejectHtml(line, _descriptor, request);
                if (line.Length == 0)
                    continue;

                foreach (var evt in ProviderResponse.Parse(() => ParseStreamLine(line, _descriptor.Id, calls), _descriptor, request))
                {
                    finished |= evt is FinishDelta;
                    yield return evt;
                }
            }

            // See StreamEnd: an answer that stops without "done": true did not finish.
            if (!finished)
                throw StreamEnd.Unfinished(_descriptor.Id, "\"done\": true");
        }
        finally
        {
            if (raw is not null && _log.IsLoggingEnabled(LogLevel.Trace))
                WireTap.Response(_log, _descriptor.Id, (int)response.StatusCode, raw.ToString(), streamed: true);
        }
    }

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        => ProviderDeadline.RunAsync(_descriptor.CompletionTimeoutSeconds, "completion", ct,
            token => CompleteCoreAsync(request, token));

    private async Task<ChatCompletion> CompleteCoreAsync(ChatRequest request, CancellationToken ct)
    {
        using var httpRequest = BuildHttpRequest(request, stream: false);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            WireTap.Error(_log, _descriptor.Id, (int)response.StatusCode, body);
            throw ProviderHttpError.Create(
                $"Provider '{_descriptor.Id}' returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 500)}", (int)response.StatusCode, ProviderHttpError.RetryAfter(response));
        }

        WireTap.Response(_log, _descriptor.Id, (int)response.StatusCode, body);
        return ProviderResponse.Parse(() => ParseCompletion(body), _descriptor, request);
    }

    private HttpRequestMessage BuildHttpRequest(ChatRequest request, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["stream"] = stream,
            ["messages"] = ToWireMessages(request.Messages).ToArray()
        };
        if (_descriptor.OllamaKeepAliveSeconds is { } keepAlive)
            payload["keep_alive"] = keepAlive;
        var maxTokens = OutputTokenBudget.Resolve(request, _descriptor);
        var contextWindow = ContextWindow(request);
        // The provider's temperature, as the other two adapters read it: the engine's, its own, or none (the server's).
        // This one sent the engine's whatever the provider said, so the setting did nothing for an Ollama model.
        var temperature = _descriptor.TemperatureFor(request);
        if (temperature is not null || contextWindow is not null || maxTokens is not null)
        {
            var options = new Dictionary<string, object?>();
            if (temperature is { } t) options["temperature"] = t;
            if (contextWindow is { } nc) options["num_ctx"] = nc;
            if (maxTokens is { } limit) options["num_predict"] = limit;
            payload["options"] = options;
        }
        if (request.Think is { } think)
            payload["think"] = think;
        if (request.Tools is { Count: > 0 } tools)
            payload["tools"] = tools.Select(ToWireTool).ToArray();

        // Structured outputs. Ollama takes the schema itself in `format` - never the
        // bare string "json", which is a trap: a small model then returns valid JSON with invented
        // keys, which is worse than prose because it parses. Schema-constrained or not at all.
        //
        // Ollama Cloud does not support this and ignores the field; a local Ollama honours it. Both
        // are fine, because the caller validates the answer either way.
        if (request.ResponseSchema is { Length: > 0 } schema && TryElement(schema) is { } element)
            payload["format"] = element;

        var url = ProviderEndpoint.Chat(_descriptor, ProviderKind.OllamaNative);
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

    private static IEnumerable<ChatStreamEvent> ParseStreamLine(string line, string providerId, StreamCallIdentity calls)
    {
        var events = new List<ChatStreamEvent>();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (StreamEnd.ErrorIn(providerId, root) is { } error)
            throw error;

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
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var (name, argsJson) = ReadFunction(tc);
                    var index = calls.Resolve(null, null, name, argsJson);
                    events.Add(new ToolCallDelta(index, Guid.NewGuid().ToString("N"), name, argsJson));
                }
            }
        }

        if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
        {
            int? prompt = root.TryGetProperty("prompt_eval_count", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : null;
            int? completion = root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : null;
            if (prompt is not null || completion is not null)
                events.Add(new UsageDelta(prompt, completion, CachedPrompt(root))
                { PromptTokensIncludeCache = CachedPrompt(root) is not null });
            events.Add(new TimingDelta(ProviderTimings.Ollama(root)));

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
            ? argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString() ?? "" : argsEl.GetRawText()
            : "{}";

        using var arguments = JsonDocument.Parse(argsJson);
        if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ollama tool arguments must be a complete JSON object.");
        return (name, argsJson);
    }

    /// <summary>The schema as JSON, or null when it is not parseable - a bad schema must not fail a run.</summary>
    private static JsonElement? TryElement(string json)
    {
        try { return WireJson.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static object ToWireTool(ToolDefinition t) => new
    {
        type = "function",
        function = new
        {
            name = t.Name,
            description = t.Description,
            parameters = WireJson.Schema(t)
        }
    };

    private static IEnumerable<object> ToWireMessages(IReadOnlyList<ChatMessage> messages)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Assistant && message.ToolCalls is { } calls)
                foreach (var call in calls) names[call.Id] = call.Name;
            string? name = null;
            if (message.ToolCallId is { } id) names.TryGetValue(id, out name);
            yield return ToWire(message, name ?? message.Name);
        }
    }

    private static object ToWire(ChatMessage m, string? toolName)
    {
        if (m.Role == ChatRole.Tool)
            {
            var result = new Dictionary<string, object?> { ["role"] = "tool", ["content"] = ControlMarkup.Neutral(m.Content) };
            if (!string.IsNullOrWhiteSpace(toolName)) result["tool_name"] = toolName;
            return result;
        }

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
                        arguments = WireJson.ArgumentsOf(tc)
                    }
                }).ToArray()
            };
        }

        return new { role = RoleString(m.Role), content = m.Role == ChatRole.User ? ControlMarkup.Neutral(m.Content) : m.Content ?? "" };
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
        return new ChatCompletion(assistant, finishReason, promptTokens, completionTokens, thinking)
        { Timings = ProviderTimings.Ollama(root), CachedPromptTokens = CachedPrompt(root),
          PromptTokensIncludeCache = CachedPrompt(root) is not null };
    }

    // Older servers/backends may report only evaluated tokens. Keep usage for accounting,
    // but calibrate context only when the explicit cache counter establishes the modern contract.
    private static int? CachedPrompt(JsonElement root)
        => root.TryGetProperty("prompt_eval_cached_count", out var value) && value.TryGetInt32(out var count)
            && count >= 0 ? count : null;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
