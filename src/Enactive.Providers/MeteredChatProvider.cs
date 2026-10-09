namespace Enactive.Providers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Providers;

/// <summary>Observes all completed calls, including planner/reviewer and their retries.</summary>
public sealed class MeteredChatProvider(IChatProvider inner, string providerId, Action<ModelCallMetrics> report) : IChatProvider
{
    public int? ContextWindow(ChatRequest r) => inner.ContextWindow(r);
    public int? AnswerReserve(ChatRequest r) => inner.AnswerReserve(r);
    public int? HandoverAtPercent(ChatRequest r) => inner.HandoverAtPercent(r);
    public int? WorkingContext(ChatRequest r) => inner.WorkingContext(r);
    public int ReasoningAllowance(ChatRequest request) => inner.ReasoningAllowance(request);
    private void Report(ChatRequest r, int? prompt, int? output, int? cached, double seconds,
        double? first, ModelTimings? timings, Guid? run)
    {
        // A display subscriber must never turn a successful request into a failed operation.
        try { report(new(providerId, r.Model, prompt, output, cached, seconds, first, timings, run)); }
        catch { }
    }
    public async Task<ChatCompletion> CompleteAsync(ChatRequest r, CancellationToken ct)
    {
        // The run the call is for, taken as it starts: the scope is the caller's (LogScope), and it is there when the call is.
        var run = Enactive.Core.Diagnostics.LogScope.Current?.Run;
        var watch = Stopwatch.StartNew();
        var result = await inner.CompleteAsync(r, ct);
        Report(r, result.PromptTokens, result.CompletionTokens, result.CachedPromptTokens,
            watch.Elapsed.TotalSeconds, null, result.Timings, run);
        return result;
    }
    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest r,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var run = Enactive.Core.Diagnostics.LogScope.Current?.Run;
        var watch = Stopwatch.StartNew();
        double? first = null;
        UsageDelta? usage = null;
        ModelTimings? timings = null;
        await foreach (var delta in inner.StreamChatAsync(r, ct))
        {
            if (delta is TextDelta or ReasoningDelta or ToolCallDelta) first ??= watch.Elapsed.TotalSeconds;
            if (delta is UsageDelta u) usage = u;
            if (delta is TimingDelta t) timings = t.Timings;
            yield return delta;
        }
        Report(r, usage?.PromptTokens, usage?.CompletionTokens, usage?.CachedPromptTokens,
            watch.Elapsed.TotalSeconds, first, timings, run);
    }
}
