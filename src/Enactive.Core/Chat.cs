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
    bool? Think = null);

/// <summary>A completed assistant turn (content and/or tool calls).</summary>
public sealed record ChatCompletion(
    ChatMessage Message,
    string? FinishReason,
    int? PromptTokens,
    int? CompletionTokens);

/// <summary>One event out of the streaming pipeline. Adapters map raw SSE to these.</summary>
public abstract record ChatStreamEvent;

/// <summary>A chunk of assistant text.</summary>
public sealed record TextDelta(string Text) : ChatStreamEvent;

/// <summary>A partial tool call. Arguments arrive incrementally and are concatenated by index.</summary>
public sealed record ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsJson) : ChatStreamEvent;

/// <summary>Token usage, when the provider reports it.</summary>
public sealed record UsageDelta(int? PromptTokens, int? CompletionTokens) : ChatStreamEvent;

/// <summary>End of the turn, carrying the finish reason.</summary>
public sealed record FinishDelta(string? Reason) : ChatStreamEvent;
