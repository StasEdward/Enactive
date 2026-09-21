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
    int? MaxTokens = null,
    /// <summary>
    /// How large a prompt this provider accepts, when somebody has said — the CONTEXT window, not
    /// <see cref="MaxTokens"/>, which caps the answer.
    ///
    /// <para>It has to be declared because nothing here can find it out; see
    /// <c>ProviderConfig.ContextWindowTokens</c>, which is where the number comes from. Null means
    /// nobody said, and then the engine does not pretend to know.</para>
    /// </summary>
    int? ContextWindowTokens = null);

/// <summary>Talks to an LLM. Implementations live in Enactive.Providers (transport stays out of Core).</summary>
public interface IChatProvider
{
    /// <summary>Streams the assistant turn token by token (SSE).</summary>
    IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct);

    /// <summary>Non-streaming completion. Kept as a fallback and for tests.</summary>
    Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct);

    /// <summary>
    /// The TOTAL token window this provider will apply to this request — prompt and generation
    /// together — when it has one it can state. Null means it does not, which is the answer for
    /// every cloud provider here: their windows are large and their max_tokens caps output only.
    ///
    /// <para>Ollama is the one that can answer, because num_ctx is exactly this: a hard ceiling on
    /// prompt plus generation. Fill it with prompt and the model is cut off mid-word with
    /// done_reason "length", which looks identical to a model that ran out of output budget and is
    /// a completely different problem. The orchestrator needs the number to keep the transcript
    /// under it, and needs the null to know when not to.</para>
    /// </summary>
    int? ContextWindow(ChatRequest request) => null;
}

/// <summary>Builds an <see cref="IChatProvider"/> for a provider id.</summary>
public interface IChatProviderFactory
{
    IChatProvider Create(string providerId);
}
