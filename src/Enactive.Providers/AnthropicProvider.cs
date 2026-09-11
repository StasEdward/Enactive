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
        // Each call needs its OWN index. Every one used to be emitted as index 0, and the orchestrator
        // merges deltas by index: two calls in one completion collapsed into a single call carrying the
        // LAST name and id with the FIRST call's arguments, and the other action vanished. A read+write
        // pair on the same path was the dangerous case — the write inherited the read's arguments.
        if (completion.Message.ToolCalls is { Count: > 0 } calls)
            for (var i = 0; i < calls.Count; i++)
                yield return new ToolCallDelta(i, calls[i].Id, calls[i].Name, calls[i].ArgumentsJson);

        // The counts were already in the response and were being thrown away here, which is why a
        // run on Claude reported no tokens at all while a local one did.
        if (completion.PromptTokens is not null || completion.CompletionTokens is not null)
            yield return new UsageDelta(completion.PromptTokens, completion.CompletionTokens);

        yield return new FinishDelta(completion.FinishReason);
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
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

        // Build + send in a local function so we can retry once without `temperature`: newer Anthropic models
        // (e.g. Opus 5.x) reject it with 400 "temperature is deprecated for this model", while older ones still
        // accept it — so we keep it by default and only drop it when the API tells us this model refuses it.
        async Task<(bool Ok, int Status, string Body)> SendAsync(
            bool includeTemperature, int maxTokens, bool includeSchema)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["max_tokens"] = maxTokens,
                ["messages"] = wire.ToArray()
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
                    ["input_schema"] = ToElement(t.JsonSchema)
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

            var url = _descriptor.BaseUrl.TrimEnd('/') + "/v1/messages";
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            WireTap.Request(_log, _descriptor.Id, request.Model, json);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", _descriptor.ApiKey ?? "");
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            // One shared helper across all three adapters, so "the provider's custom headers are
            // sent" is a single behaviour with a single test rather than three near-copies of which
            // two had gone missing.
            ProviderHeaders.Apply(httpRequest, _descriptor);

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

        // A model already known to refuse the schema is not asked again - one 400 per model, not
        // one per request.
        var includeSchema = request.ResponseSchema is { Length: > 0 }
                            && !NoStructuredOutput.ContainsKey(request.Model);

        var (ok, status, body) = await SendAsync(includeTemperature, maxTokens, includeSchema);

        // Recover from the 400s Anthropic returns for otherwise-valid requests: `temperature` is
        // deprecated on newer models, max_tokens above the model's cap (the error names the cap,
        // which we parse and remember), and a schema this model or this account will not take.
        // Bounded so we can fix at most all three.
        for (var attempt = 0; attempt < 3 && !ok && status == 400; attempt++)
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
            else if (includeSchema)
            {
                // Whatever the reason, the answer is the same: this was going to work without the
                // schema, and the schema was only ever a request. Remembered so the next call does
                // not pay for the same discovery.
                NoStructuredOutput.TryAdd(request.Model, true);
                includeSchema = false;
            }
            else
            {
                break;
            }
            (ok, status, body) = await SendAsync(includeTemperature, maxTokens, includeSchema);
        }

        if (!ok)
        {
            WireTap.Error(_log, _descriptor.Id, status, body);
            throw new HttpRequestException($"Anthropic returned {status}: {Truncate(body, 500)}");
        }

        WireTap.Response(_log, _descriptor.Id, status, body);
        return ParseCompletion(body);
    }

    /// <summary>
    /// Models that answered a schema with a 400. Static and per-process, like <c>ModelCaps</c> above
    /// and for the same reason: it is a fact about the model, not about one request.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoStructuredOutput =
        new(StringComparer.OrdinalIgnoreCase);

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
            finish, promptTokens, outputTokens, CachedPromptTokens: cached);
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
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
