namespace Enactive.Agents;

using Enactive.Core.Execution;
using Enactive.Core.Tasks;

/// <summary>Owns dispatch capacity, budget boundaries and draining sibling steps.
/// The caller owns step execution, limit reporting/abandonment, checkpoint contents and event formatting.</summary>
internal static class StepDispatcher
{
    internal static async Task<string?> RunAsync(DagScheduler scheduler, RunBudget budget, int maxParallel,
        CancellationTokenSource lifetime, Func<PlanStep, CancellationToken, Task> runStep,
        Func<string, Task> onLimit, Action complete, Func<Task<string?>>? boundary = null, Func<bool>? hold = null)
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
                // A wave waiting to be validated takes nothing new: what is running finishes, the wave is
                // validated, and only then does anything - above all what builds on it - start (Phase 6).
                // A step marks itself done a moment before its task ends, so a dependent would otherwise
                // be ready while its wave still counts as running, and slip in ahead of the validation.
                var holding = hold?.Invoke() == true;
                if (holding && inFlight.Count == 0 && boundary is not null)
                {
                    if (await boundary() is { } held && limitReason is null)
                    {
                        limitReason = held;
                        await onLimit(held);
                    }
                    holding = hold?.Invoke() == true;
                }
                foreach (var ready in holding ? [] : scheduler.NextReadyBatch(Math.Min(maxParallel - inFlight.Count, budget.RemainingSteps)))
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
                // A boundary that says the run cannot go on stops it the way a limit does: nothing more is
                // dispatched, and what was waiting is accounted for.
                if (inFlight.Count == 0 && boundary is not null && await boundary() is { } stop && limitReason is null)
                {
                    limitReason = stop;
                    await onLimit(stop);
                }
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
