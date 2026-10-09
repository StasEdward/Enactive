namespace Enactive.Core.Chat;

/// <summary>Server timings for one request, in seconds; absent values are unknown, never zero.</summary>
public sealed record ModelTimings(double? PromptSeconds = null, double? GenerationSeconds = null,
    double? LoadSeconds = null, int? GenerationTokens = null);
public sealed record TimingDelta(ModelTimings Timings) : ChatStreamEvent;

/// <summary>One completed provider call. Client latency includes transport and queue time.</summary>
/// <param name="RunId">
/// The run the call was made for, when it was made inside one (LogScope) - so a display can show one run's calls and not
/// every call the process made: the window's panel summed the whole session, background runs included, and a person
/// reading it after a run could not tell that run's speed from the day's (2026-10-09).
/// </param>
public sealed record ModelCallMetrics(string ProviderId, string Model, int? PromptTokens,
    int? CompletionTokens, int? CachedPromptTokens, double RequestSeconds,
    double? FirstDeltaSeconds, ModelTimings? Timings, Guid? RunId = null);
