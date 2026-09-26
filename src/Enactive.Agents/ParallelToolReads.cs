namespace Enactive.Agents;

using Enactive.Core.Tools;

/// <summary>Bounded, explicitly opted-in reads. Results are returned in request order.</summary>
internal static class ParallelToolReads
{
    public const int Limit = 4;

    public static async Task<ToolInvocation.Result[]> ExecuteAsync(IReadOnlyList<ToolCall> calls, IToolRegistry tools,
        ToolContext context, CancellationToken ct)
    {
        if (calls.Count > Limit) throw new ArgumentOutOfRangeException(nameof(calls));
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Some local read/search tools do synchronous work before their first await.
        // WhenAll drains every launched task even when cancellation faults one of them.
        return await Task.WhenAll(calls.Select(call => Task.Run(async () =>
        {
            batch.Token.ThrowIfCancellationRequested();
            try { return await ToolInvocation.ExecuteAsync(call, tools, context, batch.Token); }
            catch (OperationCanceledException)
            { await batch.CancelAsync(); throw; }
        }, batch.Token)));
    }
}
