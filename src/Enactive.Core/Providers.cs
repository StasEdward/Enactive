namespace Enactive.Core.Providers;

using Enactive.Core.Chat;

/// <summary>How a provider speaks on the wire.</summary>
public enum ProviderKind { OpenAiCompatible, Anthropic, OllamaNative }

/// <summary>Points at a concrete model on a concrete provider. Core knows this — not any HTTP/SDK type.</summary>
public sealed record ModelRef(string ProviderId, string Model);

/// <summary>Configuration for one provider endpoint.</summary>
public sealed record ProviderDescriptor(
    string Id,
    string DisplayName,
    ProviderKind Kind,
    string BaseUrl,
    string? ApiKey,
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, string>? Headers = null,
    // Max output tokens for this provider (Anthropic max_tokens). Null = the provider's built-in default.
    int? MaxTokens = null);

/// <summary>Talks to an LLM. Implementations live in Enactive.Providers (transport stays out of Core).</summary>
public interface IChatProvider
{
    /// <summary>Streams the assistant turn token by token (SSE).</summary>
    IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct);

    /// <summary>Non-streaming completion. Kept as a fallback and for tests.</summary>
    Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct);
}

/// <summary>Builds an <see cref="IChatProvider"/> for a provider id.</summary>
public interface IChatProviderFactory
{
    IChatProvider Create(string providerId);
}
