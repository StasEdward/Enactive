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
    // Default output budget, used unless ChatRequest.MaxTokens overrides it. Always clamped by the
    // request's OutputTokenLimit and known model limits. Null = the adapter/server default.
    int? MaxTokens = null,
    /// <summary>
    /// How large a prompt this provider accepts, when somebody has said — the CONTEXT window, not
    /// <see cref="MaxTokens"/>, which caps the answer.
    ///
    /// <para>It has to be declared because nothing here can find it out; see
    /// <c>ProviderConfig.ContextWindowTokens</c>, which is where the number comes from. Null means
    /// nobody said, and then the engine does not pretend to know.</para>
    /// </summary>
    int? ContextWindowTokens = null,
    /// <summary>Tokens of the window held back for the answer. See <c>ProviderConfig.AnswerReserveTokens</c>.</summary>
    int? AnswerReserveTokens = null,
    /// <summary>How full the window may get before a step is handed over. See <c>ProviderConfig.HandoverAtPercent</c>.</summary>
    int? HandoverAtPercent = null,
    int StreamIdleTimeoutSeconds = 300,
    bool OpenAiReasoningProfile = false,
    int? OllamaKeepAliveSeconds = null,
    int CompletionTimeoutSeconds = 900,
    int? ReasoningTokenAllowance = null,
    /// <summary>
    /// The prompt size a step WORKS at, as distinct from the window it may never exceed. See
    /// <c>ProviderConfig.WorkingContextTokens</c>.
    /// </summary>
    int? WorkingContextTokens = null,
    /// <summary>How hard the model is asked to work (Anthropic <c>output_config.effort</c>). See <c>ProviderConfig.Effort</c>.</summary>
    string? Effort = null,
    /// <summary>The temperature this provider's models are asked for, in place of the engine's. See <c>ProviderConfig.Temperature</c>.</summary>
    double? Temperature = null,
    /// <summary>Send no temperature at all: the server's own setting applies. See <c>ProviderConfig.Temperature</c>.</summary>
    bool ServerTemperature = false,
    /// <summary>Send the model's reasoning back with its earlier turns. See <c>ProviderConfig.SendReasoningBack</c>.</summary>
    bool SendReasoningBack = false)
{
    /// <summary>The temperature to send for this request: none, where the server decides; this provider's; or the engine's.</summary>
    public double? TemperatureFor(ChatRequest request) => ServerTemperature ? null : Temperature ?? request.Temperature;
}

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

    /// <summary>
    /// How many tokens of <see cref="ContextWindow"/> to keep free for the answer, when somebody has
    /// configured it for this provider. Null means the engine's proportional default.
    /// </summary>
    int? AnswerReserve(ChatRequest request) => null;

    /// <summary>
    /// The percentage of <see cref="ContextWindow"/> at which a step is handed over to a fresh
    /// conversation, when somebody has configured it. Null means handover by turn count, as for a
    /// provider with no window at all.
    /// </summary>
    int? HandoverAtPercent(ChatRequest request) => null;

    /// <summary>
    /// The prompt size this provider's steps should WORK at, in tokens, when somebody has said -
    /// distinct from <see cref="ContextWindow"/>, the size a prompt may never exceed. Reaching it
    /// hands the step over to a fresh conversation, and an emergency trim cuts back to half of it.
    ///
    /// <para>Null means the working size is derived from the window, as it always has been: a share
    /// of it by <see cref="HandoverAtPercent"/>, or the turn count. That coupling is exactly what
    /// this exists to break - with the working size a fraction of the window, declaring a model's
    /// real, larger window would enlarge the working prompt with it, when the point of the larger
    /// window is to be held in reserve.</para>
    /// </summary>
    int? WorkingContext(ChatRequest request) => null;

    /// <summary>Additional generation allowance for reasoning; still subject to context and configured output caps.</summary>
    int ReasoningAllowance(ChatRequest request) => 0;
}

/// <summary>Builds an <see cref="IChatProvider"/> for a provider id.</summary>
public interface IChatProviderFactory
{
    IChatProvider Create(string providerId);
}
