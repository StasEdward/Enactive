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
/// Completion and SSE streaming share request construction and capability negotiation.
/// </summary>
public sealed partial class AnthropicProvider : IChatProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 32000;
    private static readonly JsonSerializerOptions JsonOpts = new();

    // Discovered per-model output caps: on a 400 saying our max_tokens exceeds the model's limit, we parse
    // the real maximum from the error and remember it, so later calls to that model request the right size.
    private readonly ConcurrentDictionary<string, int> ModelCaps = new();

    private readonly HttpClient _http;
    private readonly ProviderDescriptor _descriptor;
    private readonly ILogSink? _log;

    public AnthropicProvider(HttpClient http, ProviderDescriptor descriptor, ILogSink? log = null)
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

    public int? AnswerReserve(ChatRequest request) => _descriptor.AnswerReserveTokens;

    public int? HandoverAtPercent(ChatRequest request) => _descriptor.HandoverAtPercent;
    public int? WorkingContext(ChatRequest request) => _descriptor.WorkingContextTokens;
    public int ReasoningAllowance(ChatRequest request) => Math.Clamp(_descriptor.ReasoningTokenAllowance ?? (false ? 8192 : 0), 0, 65536);

    private HttpRequestMessage BuildHttpRequest(ChatRequest request, bool stream, bool includeTemperature, int maxTokens, bool includeSchema)
    {
        var systemParts = new List<string>();
        var wire = new List<Dictionary<string, object?>>();

        // Content blocks are dictionaries rather than anonymous types for one reason: a cache
        // breakpoint is a property added to ONE of them after the whole list is built, and there is
        // no way to add a property to an anonymous type.
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.System:
                    if (!string.IsNullOrEmpty(m.Content))
                        systemParts.Add(m.Content);
                    break;

                case ChatRole.Tool:
                    wire.Add(Turn("user", new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = m.ToolCallId ?? "",
                        ["content"] = m.Content ?? ""
                    }));
                    break;

                case ChatRole.Assistant when m.ToolCalls is { Count: > 0 } calls:
                    var blocks = new List<Dictionary<string, object?>>();
                    if (!string.IsNullOrEmpty(m.Content))
                        blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content });
                    foreach (var call in calls)
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = ToElement(call.ArgumentsJson)
                        });
                    wire.Add(Turn("assistant", blocks.ToArray()));
                    break;

                default:
                    if (string.IsNullOrWhiteSpace(m.Content)) break;
                    wire.Add(Turn(
                        m.Role == ChatRole.Assistant ? "assistant" : "user",
                        new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content ?? "" }));
                    break;
            }
        }

        // ── Cache breakpoints ────────────────────────────────────────────────
        //
        // Anthropic forms prefixes in the order tools -> system -> messages, and a change at any
        // level invalidates that level and every level after it. So the breakpoints go where the
        // content is FIXED for longest: the tool definitions (unchanged for a whole run), then the
        // system prompt (likewise), then the end of the transcript (where the growth is, and where
        // the money is - a twelve-step run re-sends the same prefix a few dozen times).
        //
        // Four are allowed and three are used, which leaves room for a fourth without a rewrite.
        //
        // Nothing is guarded on length. A prompt below the model's minimum - 1,024 tokens on Sonnet,
        // 512 on Opus 5 - is simply processed without caching and costs nothing extra; there is no
        // error and no penalty, so a guess about the token count here would add a rule that only
        // ever gets it wrong.
        MarkForCaching(wire.Count > 0 ? LastBlockOf(wire[^1]) : null);

            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["max_tokens"] = maxTokens,
                ["messages"] = wire.ToArray(),
                ["stream"] = stream
            };
            // An ARRAY of blocks, not a string. A string cannot carry cache_control, and the system
            // prompt is the second-largest fixed thing in every request.
            if (systemParts.Count > 0)
            {
                var system = new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = string.Join("\n", systemParts)
                };
                MarkForCaching(system);
                payload["system"] = new object[] { system };
            }

            if (includeTemperature && request.Temperature is { } temperature)
                payload["temperature"] = temperature;

            if (request.Tools is { Count: > 0 } tools)
            {
                // The breakpoint goes on the LAST definition, because it caches everything before
                // it: tools are one prefix, and the tool set does not change within a run.
                var defined = tools.Select(t => new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = WireJson.Schema(t)
                }).ToArray();

                MarkForCaching(defined[^1]);
                payload["tools"] = defined;
            }

            // Structured outputs (FIX_PLAN §9c). GA and not beta-gated, and compatible with tool
            // use - but incompatible with prefill and with citations, and the schema subset is
            // narrower than the tool-use one. So it is sent hopefully and dropped on refusal below,
            // never depended on.
            if (includeSchema && request.ResponseSchema is { Length: > 0 } schema)
                payload["output_config"] = new
                {
                    format = new { type = "json_schema", schema = ToElement(schema) }
                };

            var url = ProviderEndpoint.Chat(_descriptor, ProviderKind.Anthropic);
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            WireTap.Request(_log, _descriptor.Id, request.Model, json);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", _descriptor.ApiKey ?? "");
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            // One shared helper across all three adapters, so "the provider's custom headers are
            // sent" is a single behaviour with a single test rather than three near-copies of which
            // two had gone missing.
            ProviderHeaders.Apply(httpRequest, _descriptor);

            return httpRequest;
    }

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        => ProviderDeadline.RunAsync(_descriptor.CompletionTimeoutSeconds, "completion", ct,
            token => CompleteCoreAsync(request, token));

    private async Task<ChatCompletion> CompleteCoreAsync(ChatRequest request, CancellationToken ct)
    {
        using var response = await SendConfiguredAsync(request, false, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        WireTap.Response(_log, _descriptor.Id, (int)response.StatusCode, body);
        return ProviderResponse.Parse(() => ParseCompletion(body), _descriptor, request);
    }

    private readonly ConcurrentDictionary<string, bool> NoStructuredOutput = new(StringComparer.Ordinal);
    // Scoped to this endpoint/configuration instance, never a global model-name assumption.
    private readonly ConcurrentDictionary<string, bool> NoTemperature = new(StringComparer.Ordinal);

    private async Task<HttpResponseMessage> SendConfiguredAsync(ChatRequest request, bool stream, CancellationToken ct)
    {
        var includeTemperature = !NoTemperature.ContainsKey(request.Model);
        var temperatureRejected = false;
        int? learnedCap = null;
        var wantedSchema = request.ResponseSchema is { Length: > 0 };
        var schemaTooComplex = false;
        var includeSchema = wantedSchema && !NoStructuredOutput.ContainsKey(request.Model);
        var maxTokens = OutputTokenBudget.Resolve(request, _descriptor, DefaultMaxTokens,
            ModelCaps.TryGetValue(request.Model, out var known) ? known : null)!.Value;
        for (var attempt = 0; ; attempt++)
        {
            // Preserve the requested format even when the server cannot enforce this grammar.
            // This retry does not change the caller's request or bypass its domain validator.
            var wireRequest = wantedSchema && !includeSchema
                ? request with { Messages = request.Messages.Concat(new[] {
                    ChatMessage.System("Return only JSON conforming to this response schema. "
                        + "The application validates the complete response: " + request.ResponseSchema) }).ToArray() }
                : request;
            using var message = BuildHttpRequest(wireRequest, stream, includeTemperature, maxTokens, includeSchema);
            var response = stream
                ? await ProviderDeadline.HeadersAsync(_http, message, _descriptor.StreamIdleTimeoutSeconds, ct)
                : await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            if (response.IsSuccessStatusCode)
            {
                if (temperatureRejected) NoTemperature[request.Model] = true;
                if (wantedSchema && !includeSchema && !schemaTooComplex) NoStructuredOutput[request.Model] = true;
                if (learnedCap is { } confirmedCap) ModelCaps[request.Model] = confirmedCap;
                return response;
            }
            using (response)
            {
                var body = await ProviderHttpError.ReadBodyAsync(response, _descriptor.StreamIdleTimeoutSeconds, ct);
                if (attempt < 3 && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    if (includeTemperature && request.Temperature is not null && IsTemperatureDeprecated(body))
                    { includeTemperature = false; temperatureRejected = true; continue; }
                    if (TryParseMaxTokensCap(body, maxTokens, out var cap))
                    { maxTokens = cap; learnedCap = cap; continue; }
                    if (includeSchema && IsGrammarTooComplex(body))
                    {
                        _log.Warn(LogSource.Llm, "Anthropic rejected the response grammar as too complex; retrying this request with the schema in the prompt. Local response validation remains required.",
                            category: _descriptor.Id);
                        schemaTooComplex = true;
                        includeSchema = false;
                        continue;
                    }
                    if (includeSchema && (OpenAiCompatibleProvider.UnsupportedField(body, "output_config", "json_schema")
                        || OpenAiCompatibleProvider.UnsupportedField(body, "output_config.format", "json_schema")
                        || OpenAiCompatibleProvider.UnsupportedField(body, "schema", "structured output")))
                    { includeSchema = false; continue; }
                }
                WireTap.Error(_log, _descriptor.Id, (int)response.StatusCode, body);
                throw ProviderHttpError.Create($"Anthropic returned {(int)response.StatusCode}: {Truncate(body, 500)}",
                    (int)response.StatusCode, ProviderHttpError.RetryAfter(response));
            }
        }
    }
    private static bool IsGrammarTooComplex(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out var type) && type.GetString() == "invalid_request_error"
                && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                && message.GetString()!.StartsWith("The compiled grammar is too large", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsTemperatureDeprecated(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            return error.GetProperty("type").GetString() == "invalid_request_error"
                && error.GetProperty("message").GetString() is { } message
                && message.Trim().Equals("`temperature` is deprecated for this model.", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    /// <summary>
    /// Detects the "max_tokens above the model's cap" 400 and extracts the allowed maximum. The message
    /// reads like "... 32000 &gt; 64000, which is the maximum ..." — the number after '&gt;' is the cap.
    /// Context-window errors and unrecognised messages do not establish an output cap.
    /// </summary>
    private static bool TryParseMaxTokensCap(string body, int requested, out int cap)
    {
        cap = 0;
        if (body.Contains("context", StringComparison.OrdinalIgnoreCase)
            || body.Contains("input tokens", StringComparison.OrdinalIgnoreCase)
            || body.Contains("prompt", StringComparison.OrdinalIgnoreCase))
            return false;
        var m = Regex.Match(body, @"\bmax_tokens\b\s*(?:must not be|must be less than or equal to|must be <=|is too large:?|:)?\s*(?:\d+\s*)?>\s*(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        cap = int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
        return cap > 0 && cap < requested;
    }

    private static ChatCompletion ParseCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var text = new StringBuilder();
        List<ToolCall>? toolCalls = null;

        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Provider response contained no content array.");
        if (content.ValueKind == JsonValueKind.Array)
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
        RequireCompletedTurn(finish);

        // ── What the turn actually cost ──────────────────────────────────────
        //
        // THREE fields, not one. With a cache breakpoint in the request, `input_tokens` counts only
        // what follows the LAST breakpoint; the rest of the prompt is in cache_read_input_tokens
        // (served from the cache, billed at 0.1x) and cache_creation_input_tokens (written to it,
        // billed at 1.25x). Reading input_tokens alone would report a fraction of what was spent -
        // and the run would look CHEAPER precisely because the accounting had broken, which is the
        // one direction a bug in a cost number must never fail.
        //
        // The three are summed into PromptTokens so every consumer keeps meaning what it meant; the
        // cached share rides along separately, because it is the only way to see the feature is on.
        int? inputTokens = null, outputTokens = null, cached = null, created = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) inputTokens = itv;
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) outputTokens = otv;
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt32(out var crv)) cached = crv;
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt32(out var ccv)) created = ccv;
        }

        // Null when the response reported nothing at all - "this provider does not count" and "this
        // turn cost zero" are different facts and are shown differently.
        int? promptTokens = inputTokens is null && cached is null && created is null
            ? null
            : (inputTokens ?? 0) + (cached ?? 0) + (created ?? 0);

        var contentText = text.Length > 0 ? text.ToString() : null;
        return new ChatCompletion(
            new ChatMessage(ChatRole.Assistant, contentText, toolCalls),
            finish, promptTokens, outputTokens, CachedPromptTokens: cached) { CacheCreationPromptTokens = created };
    }

    private static void RequireCompletedTurn(string? finish)
    {
        // Resuming requires replaying all server-tool/thinking blocks unchanged. Our text/tool
        // transcript cannot do that; do not silently turn this suspended response into success.
        if (finish == "pause_turn")
            throw new InvalidDataException("Anthropic returned pause_turn: the turn is incomplete. "
                + "Continuation with server-tool content blocks is not supported by this adapter.");
        if (finish == "refusal")
            throw new InvalidDataException("Anthropic refused the response (stop_reason=refusal).");
    }

    /// <summary>One turn on the wire: a role and its content blocks.</summary>
    private static Dictionary<string, object?> Turn(string role, params Dictionary<string, object?>[] blocks)
        => new() { ["role"] = role, ["content"] = blocks };

    /// <summary>
    /// The last content block of a turn — where a breakpoint for the transcript belongs, because a
    /// prefix ends at a block and the end of the last turn is the end of everything sent.
    /// </summary>
    private static Dictionary<string, object?>? LastBlockOf(Dictionary<string, object?> turn)
        => turn.TryGetValue("content", out var content)
           && content is Dictionary<string, object?>[] { Length: > 0 } blocks
            ? blocks[^1]
            : null;

    /// <summary>
    /// Puts a cache breakpoint on a block: everything up to and including it is cached.
    ///
    /// <para>Five minutes, the default, and deliberately not an hour. An hour costs twice a normal
    /// input token to write instead of 1.25x, and only pays back if runs in one workspace come
    /// close together — which is a thing to measure on a real machine, not to decide here. Steps
    /// follow each other in seconds, so five minutes covers the case this exists for.</para>
    /// </summary>
    private static void MarkForCaching(Dictionary<string, object?>? block)
    {
        if (block is not null)
            block["cache_control"] = new { type = "ephemeral" };
    }

    private static JsonElement ToElement(string json)
    {
        try { return WireJson.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return WireJson.Parse("{}"); }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
