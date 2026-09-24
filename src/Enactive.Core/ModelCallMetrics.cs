namespace Enactive.Core.Chat;

/// <summary>Server timings for one request, in seconds; absent values are unknown, never zero.</summary>
public sealed record ModelTimings(double? PromptSeconds = null, double? GenerationSeconds = null,
    double? LoadSeconds = null, int? GenerationTokens = null);
public sealed record TimingDelta(ModelTimings Timings) : ChatStreamEvent;

/// <summary>One completed provider call. Client latency includes transport and queue time.</summary>
public sealed record ModelCallMetrics(string ProviderId, string Model, int? PromptTokens,
    int? CompletionTokens, int? CachedPromptTokens, double RequestSeconds,
    double? FirstDeltaSeconds, ModelTimings? Timings);
