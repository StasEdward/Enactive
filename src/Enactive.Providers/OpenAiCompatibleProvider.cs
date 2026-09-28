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
    private readonly string _capabilityScope;

    public OpenAiCompatibleProvider(HttpClient http, ProviderDescriptor descriptor, ILogSink? log = null)
    {
        _http = http;
        _descriptor = descriptor with { Headers = descriptor.Headers?.ToDictionary(x => x.Key, x => x.Value), Models = descriptor.Models.ToArray() };
        _capabilityScope = Hash(JsonSerializer.Serialize(_descriptor));
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

    public int? AnswerReserve(ChatRequest request) => _descriptor.AnswerReserveTokens;

    public int? HandoverAtPercent(ChatRequest request) => _descriptor.HandoverAtPercent;
    public int? WorkingContext(ChatRequest request) => _descriptor.WorkingContextTokens;
    public int ReasoningAllowance(ChatRequest request) => Math.Clamp(_descriptor.ReasoningTokenAllowance ?? (_descriptor.OpenAiReasoningProfile ? 8192 : 0), 0, 65536);

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = CapabilityKey(request);
        var wantUsage = !IsDisabled(NoStreamUsage, key);
        var response = await SendAsync(wantUsage);
        string? cachedError = null;
        try
        {
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && wantUsage)
            {
                var error = cachedError = await ProviderHttpError.ReadBodyAsync(response, _descriptor.StreamIdleTimeoutSeconds, ct);
                if (UnsupportedField(error, "stream_options", "include_usage"))
                {
                    response.Dispose();
                    response = await SendAsync(includeUsage: false);
                    cachedError = null;
                    if (response.IsSuccessStatusCode)
                        NoStreamUsage[key] = DateTimeOffset.UtcNow.AddMinutes(15);
                }
            }
        }
        catch { response.Dispose(); throw; }

        using var _ = response;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = cachedError ?? await ProviderHttpError.ReadBodyAsync(response, _descriptor.StreamIdleTimeoutSeconds, ct);
            WireTap.Error(_log, _descriptor.Id, (int)response.StatusCode, errorBody);
            throw ProviderHttpError.Create(
                $"Provider '{_descriptor.Id}' returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(errorBody, 500)}", (int)response.StatusCode, ProviderHttpError.RetryAfter(response));
        }

        async Task<HttpResponseMessage> SendAsync(bool includeUsage)
        {
            using var message = BuildHttpRequest(request, stream: true, includeUsage: includeUsage);
            return await ProviderDeadline.HeadersAsync(_http, message, _descriptor.StreamIdleTimeoutSeconds, ct);
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
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var data = line["data:".Length..].Trim();
                if (data.Length == 0)
                    continue;
                if (data == "[DONE]")
                {
                    finished = true;
                    break;
                }

                foreach (var evt in ProviderResponse.Parse(() => ParseStreamChunk(data, _descriptor.Id, calls), _descriptor, request))
                {
                    finished |= evt is FinishDelta;
                    yield return evt;
                }
            }

            // See StreamEnd: an answer with neither a finish_reason nor [DONE] did not finish.
            if (!finished)
                throw StreamEnd.Unfinished(_descriptor.Id, "finish_reason and no [DONE]");
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
        TimeSpan? retryAfter = null;
        var key = CapabilityKey(request);
        var parsedSchema = request.ResponseSchema is { Length: > 0 } schema
            && !IsDisabled(NoStructuredOutput, key) ? TryElement(schema) : null;
        var wanted = parsedSchema is not null;
        var (ok, status, body) = await SendAsync(wanted);
        if (!ok && status == 400 && wanted && UnsupportedField(body, "response_format", "json_schema"))
        {
            (ok, status, body) = await SendAsync(includeSchema: false);
            if (ok) NoStructuredOutput[key] = DateTimeOffset.UtcNow.AddMinutes(15);
        }

        if (!ok)
        {
            WireTap.Error(_log, _descriptor.Id, status, body);
            throw ProviderHttpError.Create(
                $"Provider '{_descriptor.Id}' returned {status}: {Truncate(body, 500)}", status, retryAfter);
        }

        WireTap.Response(_log, _descriptor.Id, status, body);
        return ProviderResponse.Parse(() => ParseCompletion(body), _descriptor, request);

        async Task<(bool Ok, int Status, string Body)> SendAsync(bool includeSchema)
        {
            using var httpRequest = BuildHttpRequest(request, stream: false, includeSchema, responseSchema: parsedSchema);
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
            retryAfter = ProviderHttpError.RetryAfter(response);
            var text = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, text);
        }
    }

    // Successful fallbacks are scoped to the complete configured endpoint and model,
    // and expire so a server upgraded during a session can regain its capabilities.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>
        NoStructuredOutput = new(StringComparer.Ordinal), NoStreamUsage = new(StringComparer.Ordinal);

    private static bool IsDisabled(
        System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> cache, string key)
        => cache.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow;

    private string CapabilityKey(ChatRequest request)
    {
        // Immutable descriptor is hashed once. HttpClient headers remain mutable and must still
        // participate in the key: changing authentication/routing must trigger a fresh probe.
        var headers = JsonSerializer.Serialize(_http.DefaultRequestHeaders
            .Select(h => new { h.Key, Values = h.Value.ToArray() }).ToArray());
        return _capabilityScope + ":" + request.Model + ":" + Hash(headers);
    }

    private static string Hash(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static bool UnsupportedField(string body, params string[] fields)
    {
        string message = body;
        string? param = null, code = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) ? e : root;
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = m.GetString()!;
                if (error.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String)
                    param = p.GetString();
                if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    code = c.GetString();
            }
        }
        catch (JsonException) { }
        foreach (var field in fields)
        {
            if (param == field && code is "unsupported_parameter" or "unknown_parameter") return true;
            var name = System.Text.RegularExpressions.Regex.Escape(field);
            var pattern = @"\b(?:unknown|unrecognized|unsupported|unexpected)\s+(?:(?:field|parameter|argument)\s*:?\s*)?['""`]?"
                + name + @"\b|\b" + name + @"['""`]?\s+(?:type\s+)?(?:(?:is|are)\s+)?(?:not supported|unsupported|not allowed|not recognized|unavailable)\b";
            if (System.Text.RegularExpressions.Regex.IsMatch(message, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The schema as JSON, or null when it is not parseable - a bad schema must not fail a run.</summary>
    private static JsonElement? TryElement(string json)
    {
        try { return WireJson.Parse(json); }
        catch (JsonException) { return null; }
    }

    private HttpRequestMessage BuildHttpRequest(
        ChatRequest request, bool stream, bool includeSchema = true, bool includeUsage = true,
        JsonElement? responseSchema = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["stream"] = stream,
            ["messages"] = request.Messages.Select(ToWire).ToArray()
        };

        // A STREAMED response carries no usage block unless it is asked for. That is the
        // specification, not an oddity: OpenAI added stream_options.include_usage for exactly this,
        // and a server that follows it sends nothing without the field.
        //
        // Measured 2026-09-23 20:44 - a run whose worker was llama.cpp ran two steps over three and
        // a half minutes and reported not one token. The only UsageReported events in it came from
        // the planner and the reviewer, which go NON-streamed and therefore always carry usage, so
        // the work split showed the run as 100% cloud while the work was happening on this machine.
        // DeepSeek sends usage in a stream anyway, beyond the spec, which is why this stayed hidden
        // until a local endpoint was bound to a phase.
        //
        // Sent only while streaming, and only until an endpoint refuses it - see NoStreamUsage.
        if (stream && includeUsage)
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        if (!_descriptor.OpenAiReasoningProfile && request.Temperature is { } temperature)
            payload["temperature"] = temperature;
        if (request.Tools is { Count: > 0 } tools)
            payload["tools"] = tools.Select(ToWireTool).ToArray();
        if (request.RequireToolCall && request.Tools is { Count: > 0 })
            payload["tool_choice"] = "required";

        // The request wins over the provider's configured default; neither was being sent at all, so
        // the "Max tokens" field in the provider editor did nothing on this adapter. A provider that
        // caps output low would silently truncate every answer and the setting meant to raise it was
        // never on the wire.
        if (OutputTokenBudget.Resolve(request, _descriptor) is { } maxTokens)
            payload[_descriptor.OpenAiReasoningProfile ? "max_completion_tokens" : "max_tokens"] = maxTokens;

        // Structured outputs (FIX_PLAN §9c). "OpenAI-compatible" is a family, not a specification:
        // vLLM and LM Studio take this, and an arbitrary gateway may ignore it or reject the whole
        // request for it. Sent only when the caller asked and this endpoint has not already refused
        // one. Only an explicit unsupported-field error triggers a fallback; only a successful
        // fallback is cached.
        if (includeSchema && request.ResponseSchema is { Length: > 0 } schema && (responseSchema ?? TryElement(schema)) is { } element)
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "answer", schema = element, strict = false }
            };

        var url = ProviderEndpoint.Chat(_descriptor, ProviderKind.OpenAiCompatible);
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

    private static IEnumerable<ChatStreamEvent> ParseStreamChunk(string data, string providerId, StreamCallIdentity calls)
    {
        var events = new List<ChatStreamEvent>();
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (StreamEnd.ErrorIn(providerId, root) is { } error)
            throw error;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];

            if (choice.TryGetProperty("delta", out var delta))
            {
                if (Reasoning(delta) is { Length: > 0 } thinking)
                    events.Add(new ReasoningDelta(thinking));
                if (ResponseText(delta) is { Length: > 0 } text)
                {
                    events.Add(new TextDelta(text));
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        int? wireIndex = tc.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : null;
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

                        var index = calls.Resolve(wireIndex, id, name, argumentsPart);
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

        if (ProviderTimings.OpenAi(root) is { } timings) events.Add(new TimingDelta(timings));
        return events;
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

    private object ToWire(ChatMessage m)
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

        return new { role = _descriptor.OpenAiReasoningProfile && m.Role == ChatRole.System
            ? "developer" : RoleString(m.Role), content = m.Content ?? "" };
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

        var content = ResponseText(message);

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
        return new ChatCompletion(assistant, finishReason, promptTokens, completionTokens, Thinking: Reasoning(message))
        {
            CachedPromptTokens = cached,
            Timings = ProviderTimings.OpenAi(root)
        };
    }

    // Refusals must never become an empty successful answer (including structured review).
    private static string? ResponseText(JsonElement message)
    {
        if (message.TryGetProperty("refusal", out var refusal)
            && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(refusal.GetString()))
            throw new InvalidDataException("OpenAI-compatible provider refused the response (refusal): " + refusal.GetString());
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
            return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OpenAI-compatible response content must be text or an array of text blocks.");
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            if (type == "refusal")
                throw new InvalidDataException("OpenAI-compatible provider refused the response (refusal): "
                    + (block.TryGetProperty("refusal", out var reason) ? reason.GetString() : "unspecified"));
            if (type != "text")
                throw new InvalidDataException("OpenAI-compatible response contains an unsupported content block.");
            text.Append(block.GetProperty("text").GetString());
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static string? Reasoning(JsonElement message)
    {
        foreach (var name in new[] { "reasoning_content", "reasoning" })
            if (message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text) return text;
        return null;
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
