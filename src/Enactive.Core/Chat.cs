namespace Enactive.Core.Chat;

using Enactive.Core.Tools;

/// <summary>Role of a chat message.</summary>
public enum ChatRole { System, User, Assistant, Tool }

/// <summary>A single chat message. Assistant messages may carry tool calls.</summary>
public sealed record ChatMessage(
    ChatRole Role,
    string? Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null)
{
    public static ChatMessage System(string content) => new(ChatRole.System, content);
    public static ChatMessage User(string content) => new(ChatRole.User, content);
    public static ChatMessage Assistant(string? content, IReadOnlyList<ToolCall>? toolCalls = null)
        => new(ChatRole.Assistant, content, toolCalls);
    public static ChatMessage Tool(string toolCallId, string content)
        => new(ChatRole.Tool, content, ToolCallId: toolCallId);
}

/// <summary>A non-streaming chat request.</summary>
public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition>? Tools = null,
    double? Temperature = null,
    int? MaxTokens = null,
    // Ollama context window override (options.num_ctx on the wire). Only honored by
    // OllamaNativeProvider - the OpenAI-compatible /v1 endpoint has no such field and
    // silently ignores it (confirmed against Ollama's own openai/openai.go).
    int? NumCtx = null,
    // Ollama "think" toggle (top-level on /api/chat). Set false to stop a reasoning model (qwen3, ...)
    // from spending a whole turn in <think> and returning empty content. Null = leave it to the model.
    // Only OllamaNativeProvider honors it; other providers ignore it.
    bool? Think = null,
    // A JSON Schema the answer should take, as text. A REQUEST, not a contract: there is no single
    // field for this across providers (Anthropic output_config.format, Ollama format, OpenAI-style
    // response_format), some gateways reject the field outright, and a model may ignore it anyway.
    // Every adapter that cannot satisfy it ignores it, and one that is refused for it retries
    // without it - see FIX_PLAN §9c.
    //
    // It never becomes the thing correctness rests on. The caller PARSES AND VALIDATES the answer
    // exactly as it did before; this only makes the unparseable path rarer. An unreachable reviewer
    // does not count as PASS, and a schema the model ignored does not count as a verdict.
    string? ResponseSchema = null);

/// <summary>A completed assistant turn (content and/or tool calls).</summary>
/// <param name="PromptTokens">
/// EVERY input token this turn was billed for.
///
/// <para>Under prompt caching that is a sum rather than a field. Anthropic's <c>input_tokens</c>
/// counts only what follows the last cache breakpoint, with the rest in
/// <c>cache_read_input_tokens</c> and <c>cache_creation_input_tokens</c>; reading the one field
/// would report a fraction of what was spent, and the number would look BETTER precisely because
/// the accounting had broken. The adapter sums the three, so every consumer of this keeps meaning
/// what it meant.</para>
/// </param>
/// <param name="CachedPromptTokens">
/// How many of <paramref name="PromptTokens"/> were served from a cache, when the provider says so.
///
/// <para>Null for a provider that does not report it, which is a different fact from zero: zero
/// means caching was attempted and missed, and null means nobody asked. It is also the only honest
/// way to see the feature is working at all — a run whose prefix is being cached and a run whose is
/// not look identical from every other number.</para>
/// </param>
public sealed record ChatCompletion(
    ChatMessage Message,
    string? FinishReason,
    int? PromptTokens,
    int? CompletionTokens,
    /// <summary>The model's own deliberation, where the provider returns it separately. See <see cref="ReasoningDelta"/>.</summary>
    string? Thinking = null,
    int? CachedPromptTokens = null);

/// <summary>One event out of the streaming pipeline. Adapters map raw SSE to these.</summary>
public abstract record ChatStreamEvent;

/// <summary>A chunk of assistant text.</summary>
public sealed record TextDelta(string Text) : ChatStreamEvent;

/// <summary>A partial tool call. Arguments arrive incrementally and are concatenated by index.</summary>
public sealed record ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsJson) : ChatStreamEvent;

/// <summary>Token usage, when the provider reports it.</summary>
public sealed record UsageDelta(int? PromptTokens, int? CompletionTokens) : ChatStreamEvent;

/// <summary>
/// A reasoning model's own deliberation, which some providers return in a field of its own rather
/// than as content.
///
/// <para>It is NOT content and must never be appended to the transcript as an answer - it is a
/// draft, complete with what the model was considering and rejecting. It is carried because losing
/// it entirely is worse: a turn that spends every token thinking then arrives as content=null with
/// no tool calls, which reads exactly like a model that had nothing to say. On 2026-09-07 that ended
/// a run twice with "no tools were run and no files were changed" - a true sentence about a
/// symptom, with the cause nowhere in the log.</para>
/// </summary>
public sealed record ReasoningDelta(string Text) : ChatStreamEvent;

/// <summary>End of the turn, carrying the finish reason.</summary>
public sealed record FinishDelta(string? Reason) : ChatStreamEvent;
