namespace Enactive.Providers;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;
using Enactive.Core.Tools;

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
    private readonly ILogSink? _log;

    public OpenAiCompatibleProvider(HttpClient http, ProviderDescriptor descriptor, ILogSink? log = null)
    {
        _http = http;
        _descriptor = descriptor;
        _log = log;
    }


    /// <summary>
    /// The prompt window this provider was DECLARED to have, or null when nobody said.
    ///
    /// <para>Only Ollama could answer this, by echoing back the num_ctx it was handed - a mirror
    /// of the request rather than a fact about the model. So the guard that trims a transcript to
    /// fit never applied to a cloud provider at all, and a run's conversation grew without limit:
    /// measured 2026-09-21, prompts reached 176,000 tokens and ContextTrimmed fired zero times in
    /// a run that spent 12.4M.</para>
    ///
    /// <para>Null still means null. A number that is wrong in the generous direction costs a
    /// failed request, which is why nothing is inferred from a model's name - the person says it
    /// in the provider editor or nobody does.</para>
    /// </summary>
    public int? ContextWindow(ChatRequest request) => _descriptor.ContextWindowTokens;

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
        finally
        {
            WireTap.Response(_log, _descriptor.Id, (int)response.StatusCode, raw.ToString(), streamed: true);
        }
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        // A schema is a REQUEST here more than anywhere else: "OpenAI-compatible" is a family rather
        // than a specification, and an endpoint that does not know response_format may reject the
        // whole call for it. So: send it, and if the answer is a 400, send the same request again
        // without it and remember. The cost of being wrong is one round trip; the cost of not
        // trying is that the field is useless on every gateway that DOES support it.
        var wanted = request.ResponseSchema is { Length: > 0 } && !NoStructuredOutput.ContainsKey(SchemaKey(request));

        var (ok, status, body) = await SendAsync(wanted);

        if (!ok && status == 400 && wanted)
        {
            NoStructuredOutput.TryAdd(SchemaKey(request), true);
            (ok, status, body) = await SendAsync(includeSchema: false);
        }

        if (!ok)
        {
            WireTap.Error(_log, _descriptor.Id, status, body);
            throw new HttpRequestException(
                $"Provider '{_descriptor.Id}' returned {status}: {Truncate(body, 500)}");
        }

        WireTap.Response(_log, _descriptor.Id, status, body);
        return ParseCompletion(body);

        async Task<(bool Ok, int Status, string Body)> SendAsync(bool includeSchema)
        {
            using var httpRequest = BuildHttpRequest(request, stream: false, includeSchema);
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, text);
        }
    }

    /// <summary>
    /// Endpoints that answered a schema with a 400, by provider and model. Static and per-process,
    /// like the Anthropic adapter's cap table: it is a fact about the endpoint, not about one call.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoStructuredOutput =
        new(StringComparer.OrdinalIgnoreCase);

    private string SchemaKey(ChatRequest request) => _descriptor.Id + "\0" + request.Model;

    /// <summary>The schema as JSON, or null when it is not parseable - a bad schema must not fail a run.</summary>
    private static JsonElement? TryElement(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private HttpRequestMessage BuildHttpRequest(ChatRequest request, bool stream, bool includeSchema = true)
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

        // The request wins over the provider's configured default; neither was being sent at all, so
        // the "Max tokens" field in the provider editor did nothing on this adapter. A provider that
        // caps output low would silently truncate every answer and the setting meant to raise it was
        // never on the wire.
        if ((request.MaxTokens ?? _descriptor.MaxTokens) is { } maxTokens and > 0)
            payload["max_tokens"] = maxTokens;

        // Structured outputs (FIX_PLAN §9c). "OpenAI-compatible" is a family, not a specification:
        // vLLM and LM Studio take this, and an arbitrary gateway may ignore it or reject the whole
        // request for it. Sent only when the caller asked and this endpoint has not already refused
        // one, and CompleteAsync retries without it on a 400 - so the worst case is the behaviour
        // this had yesterday, one wasted round trip.
        if (includeSchema && request.ResponseSchema is { Length: > 0 } schema && TryElement(schema) is { } element)
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "answer", schema = element, strict = false }
            };

        var url = _descriptor.BaseUrl.TrimEnd('/') + "/chat/completions";
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        WireTap.Request(_log, _descriptor.Id, request.Model, json);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_descriptor.ApiKey))
            httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _descriptor.ApiKey);

        // Applied AFTER Authorization so a provider configured with its own auth header can replace
        // the default rather than fight it.
        ProviderHeaders.Apply(httpRequest, _descriptor);
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
                events.Add(new UsageDelta(prompt, completion, CachedTokens(usage)));
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

        int? promptTokens = null, completionTokens = null, cached = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv)) promptTokens = ptv;
            if (usage.TryGetProperty("completion_tokens", out var cpt) && cpt.TryGetInt32(out var cptv)) completionTokens = cptv;
            cached = CachedTokens(usage);
        }

        var assistant = new ChatMessage(ChatRole.Assistant, content, toolCalls);
        return new ChatCompletion(assistant, finishReason, promptTokens, completionTokens)
        {
            CachedPromptTokens = cached
        };
    }

    /// <summary>
    /// How much of the prompt the provider served from its own cache, or null where it does not
    /// say.
    ///
    /// <para><b>Nothing read this, and the cost of a run was therefore unknowable.</b> Measured
    /// 2026-09-21: a twelve-minute task spent 31,391,109 prompt tokens over 333 completions, and
    /// not one of them reported a cached figure - because only the Anthropic adapter looked for
    /// one, and DeepSeek, Jan, llama.cpp and LM Studio all come through here. A cache hit costs
    /// roughly a tenth of a miss, so that run's bill was unknown within a factor of ten, and no
    /// decision about the engine's appetite could be made on it.</para>
    ///
    /// <para>Two spellings, because the providers that report it disagree. OpenAI and those
    /// copying it nest it: <c>usage.prompt_tokens_details.cached_tokens</c>. DeepSeek puts it flat
    /// as <c>prompt_cache_hit_tokens</c>, beside <c>prompt_cache_miss_tokens</c>. Both mean the
    /// same thing and both are part of <c>prompt_tokens</c> already, which is what
    /// <see cref="ChatCompletion.CachedPromptTokens"/> expects - see the Anthropic adapter, where
    /// the total has to be assembled instead.</para>
    ///
    /// <para>Null and ZERO are different answers and are kept different. Zero is a provider
    /// saying it cached nothing; null is a provider that does not report caching at all. Reading
    /// the second as the first would put a confident 0% on a run nobody measured.</para>
    /// </summary>
    private static int? CachedTokens(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        // OpenAI and the adapters that copy its shape.
        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out var nested)
            && nested.TryGetInt32(out var nestedValue))
            return nestedValue;

        // DeepSeek.
        if (usage.TryGetProperty("prompt_cache_hit_tokens", out var flat)
            && flat.TryGetInt32(out var flatValue))
            return flatValue;

        return null;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
