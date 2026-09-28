namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Tasks;

/// <summary>Engine-owned scope/state, not a worker summary or permission to skip verification.</summary>
internal static class StepBoundary
{
    internal static string Describe(Plan plan, Guid active, IReadOnlyCollection<Guid> completed,
        IReadOnlyCollection<Guid>? unverified = null)
    {
        unverified ??= [];
        return "Engine-owned step boundary:\n" + JsonSerializer.Serialize(plan.Steps.Select((s, i) => new {
            scope = "S" + (i + 1),
            // "completed-unverified": that step's work was done and its review did not return a
            // verdict. The work exists and may be built on; it has not been confirmed.
            state = s.Id == active ? "active" : completed.Contains(s.Id) ? "completed"
                : unverified.Contains(s.Id) ? "completed-unverified" : "outside-current-scope"
        })) + (unverified.Count == 0 ? "" :
            "\nA completed-unverified step's work exists but its review returned no verdict: build on it, and "
            + "check what this step relies on from it rather than assuming it correct.")
            + "\nDeliver only the active objective. Do not execute later steps, mutation/cleanup/reporting assigned elsewhere "
            + "merely because the original request describes them. Stop and report the active result when done. "
            + "Use the shared O-ID map without renaming source units. If an active deliverable already exists, inspect and reuse "
            + "it rather than rewriting it to make this step look productive. Existing work is not proof: perform the checks "
            + "required in this scope or cite applicable engine history for unchanged inputs. Do not skip a requested rerun "
            + "or a check after mutation/restoration. A failed check may be repaired within this scope; a missing prerequisite "
            + "must be stated concretely, not silently expanded into the rest of the plan.\n";
    }
}
