namespace Enactive.Agents;

using Enactive.Core.Execution;
using Enactive.Core.Tasks;

/// <summary>Owns dispatch capacity, budget boundaries and draining sibling steps.
/// The caller owns step execution, limit reporting/abandonment, checkpoint contents and event formatting.</summary>
internal static class StepDispatcher
{
    internal static async Task<string?> RunAsync(DagScheduler scheduler, RunBudget budget, int maxParallel,
        CancellationTokenSource lifetime, Func<PlanStep, CancellationToken, Task> runStep,
        Func<string, Task> onLimit, Action complete, Func<Task>? boundary = null)
    {
        string? limitReason = null;
        var inFlight = new List<Task>();
        try
        {
            while (true)
            {
                // Dispatch boundaries only: a budget limit never cancels an already-running step.
                if (limitReason is null && budget.Exhausted is { } spent)
                {
                    limitReason = spent;
                    await onLimit(spent);
                }
                foreach (var ready in scheduler.NextReadyBatch(Math.Min(maxParallel - inFlight.Count, budget.RemainingSteps)))
                {
                    // Giving a step's items their steps, and joining them, is the engine's own work:
                    // no model runs, and it does not spend a step of the run's budget (Phase 5.3).
                    if (ready.ForEach is null && !ready.Joins)
                        budget.StepStarted();
                    inFlight.Add(runStep(ready, lifetime.Token));
                }
                if (inFlight.Count == 0) break;
                var finished = await Task.WhenAny(inFlight);
                inFlight.Remove(finished);
                await finished;
                // Nothing running: the end of a wave (Phase 6), before anything that builds on it starts.
                if (inFlight.Count == 0 && boundary is not null)
                    await boundary();
            }
            return limitReason;
        }
        finally
        {
            try { await lifetime.CancelAsync(); }
            finally
            {
                try { await Task.WhenAll(inFlight); }
                catch (Exception) { /* Preserve the original dispatcher failure. */ }
                complete();
            }
        }
    }
}
