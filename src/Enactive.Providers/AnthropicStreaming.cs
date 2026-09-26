namespace Enactive.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;

public sealed partial class AnthropicProvider
{
    // Protocol: https://platform.claude.com/docs/en/build-with-claude/streaming
    public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct)
        => ProviderResponse.Guard(ReadStreamAsync(request, ct), _descriptor, request, ct);

    private async IAsyncEnumerable<ChatStreamEvent> ReadStreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendConfiguredAsync(request, true, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(new IdleTimeoutStream(stream, TimeSpan.FromSeconds(Math.Clamp(_descriptor.StreamIdleTimeoutSeconds, 1, 86400))));
        var raw = BoundedLogBuffer.Create(_log, LogLevel.Trace);
        var tools = new Dictionary<int, bool>();
        var openBlocks = new HashSet<int>();
        int? prompt = null, cached = null, created = null, output = null;
        string? finish = null;
        var stopped = false;
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!_log.IsLoggingEnabled(LogLevel.Trace)) raw = null;
                raw?.AppendLine(line);
                ProviderResponse.RejectHtml(line, _descriptor, request);
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(line[5..]);
                var root = doc.RootElement;
                var type = Text(root, "type");
                switch (type)
                {
                    case "error":
                        throw StreamEnd.ErrorIn(_descriptor.Id, root)
                            ?? new HttpRequestException("Anthropic sent an error event without an error description.");
                    case "message_start":
                        if (root.GetProperty("message").TryGetProperty("usage", out var initial))
                        {
                            cached = Number(initial, "cache_read_input_tokens");
                            created = Number(initial, "cache_creation_input_tokens");
                            prompt = TokenCounts.Add(TokenCounts.Add(Number(initial, "input_tokens"), cached),
                                created);
                            output = Number(initial, "output_tokens");
                        }
                        break;
                    case "content_block_start":
                        var block = root.GetProperty("content_block");
                        var index = root.GetProperty("index").GetInt32();
                        if (!openBlocks.Add(index)) throw new InvalidDataException("Duplicate open content block.");
                        if (Text(block, "type") == "tool_use")
                        {
                            if (!tools.TryAdd(index, false)) throw new InvalidDataException("Duplicate Anthropic tool block index.");
                            var input = block.TryGetProperty("input", out var initialInput)
                                && initialInput.ValueKind == JsonValueKind.Object && initialInput.EnumerateObject().Any()
                                ? initialInput.GetRawText() : "";
                            tools[index] = input.Length > 0;
                            yield return new ToolCallDelta(index, Text(block, "id"), Text(block, "name"), input);
                        }
                        else if (Text(block, "text") is { Length: > 0 } text) yield return new TextDelta(text);
                        else if (Text(block, "thinking") is { Length: > 0 } thinking) yield return new ReasoningDelta(thinking);
                        break;
                    case "content_block_delta":
                        if (!openBlocks.Contains(root.GetProperty("index").GetInt32()))
                            throw new InvalidDataException("Content delta without an open block.");
                        var delta = root.GetProperty("delta");
                        if (Text(delta, "type") == "text_delta") yield return new TextDelta(Text(delta, "text") ?? "");
                        else if (Text(delta, "type") == "thinking_delta") yield return new ReasoningDelta(Text(delta, "thinking") ?? "");
                        else if (Text(delta, "type") == "input_json_delta")
                        {
                            var toolIndex = root.GetProperty("index").GetInt32();
                            if (!tools.ContainsKey(toolIndex)) throw new InvalidDataException("Tool delta without a tool block.");
                            tools[toolIndex] = true;
                            yield return new ToolCallDelta(toolIndex, null, null, Text(delta, "partial_json"));
                        }
                        break;
                    case "content_block_stop":
                        var ended = root.GetProperty("index").GetInt32();
                        if (!openBlocks.Remove(ended)) throw new InvalidDataException("Content stop without an open block.");
                        if (tools.TryGetValue(ended, out var hasArguments) && !hasArguments)
                            yield return new ToolCallDelta(ended, null, null, "{}");
                        break;
                    case "message_delta":
                        finish = Text(root.GetProperty("delta"), "stop_reason") ?? finish;
                        if (root.TryGetProperty("usage", out var usage)) output = Number(usage, "output_tokens") ?? output;
                        break;
                    case "message_stop": stopped = true; break;
                }
                if (stopped) break;
            }
            if (!stopped || openBlocks.Count != 0 || finish is null)
                throw StreamEnd.Unfinished(_descriptor.Id, "complete content blocks, stop_reason and message_stop");
            yield return new UsageDelta(prompt, output, cached) { CacheCreationPromptTokens = created };
            yield return new FinishDelta(finish);
        }
        finally
        {
            if (raw is not null && _log.IsLoggingEnabled(LogLevel.Trace))
                WireTap.Response(_log, _descriptor.Id, (int)response.StatusCode, raw.ToString(), streamed: true);
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
