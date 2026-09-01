namespace AIClient.Core.Orchestration;

using AIClient.Core.Events;
using AIClient.Core.Intents;

/// <summary>
/// The single seam the UI talks to. Everything starts as an Intent; the whole lifecycle
/// streams back as <see cref="WorkEvent"/>s (PLAN_v2 §4).
/// Decision/resume and cancellation arrive with the Task + Permissions stages.
/// </summary>
public interface IOrchestrator
{
    IAsyncEnumerable<WorkEvent> SubmitIntentAsync(Intent intent, CancellationToken ct);
}
