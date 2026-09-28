namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Binds requests to this run, without changing a shared provider or using ambient state.</summary>
internal sealed class RunBudgetChatProvider(IChatProvider inner, RunBudget budget) : IChatProvider
{
    public int? ContextWindow(ChatRequest request) => inner.ContextWindow(request);
    public int? AnswerReserve(ChatRequest request) => inner.AnswerReserve(request);
    public int? HandoverAtPercent(ChatRequest request) => inner.HandoverAtPercent(request);
    public int? WorkingContext(ChatRequest request) => inner.WorkingContext(request);
    public int ReasoningAllowance(ChatRequest request) => inner.ReasoningAllowance(request);
    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        => inner.CompleteAsync(request with { RetryBudget = budget }, ct);
    public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, CancellationToken ct)
        => inner.StreamChatAsync(request with { RetryBudget = budget }, ct);
}
