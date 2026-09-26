namespace Enactive.Core.Tasks;

/// <summary>Structural validation of the whole plan, independent of saved execution statuses.</summary>
public static class PlanValidation
{
    /// <summary>Null for a DAG; otherwise a diagnostic. Iterative O(V + E), including disconnected components.</summary>
    public static string? Error(Plan plan)
    {
        var indices = new Dictionary<Guid, int>();
        for (var i = 0; i < plan.Steps.Count; i++)
            if (!indices.TryAdd(plan.Steps[i].Id, i))
                return $"Plan has duplicate step IDs at steps {indices[plan.Steps[i].Id] + 1} and {i + 1}.";

        var incoming = new int[plan.Steps.Count];
        var dependents = Enumerable.Range(0, plan.Steps.Count).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < plan.Steps.Count; i++)
            foreach (var dependency in plan.Steps[i].DependsOn.Distinct())
            {
                if (!indices.TryGetValue(dependency, out var prerequisite))
                    return $"Plan step {i + 1} depends on a step missing from the plan.";
                incoming[i]++;
                dependents[prerequisite].Add(i);
            }

        var ready = new Queue<int>(Enumerable.Range(0, incoming.Length).Where(i => incoming[i] == 0));
        var visited = 0;
        while (ready.TryDequeue(out var step))
        {
            visited++;
            foreach (var dependent in dependents[step])
                if (--incoming[dependent] == 0) ready.Enqueue(dependent);
        }

        if (visited == plan.Steps.Count) return null;
        var blocked = Enumerable.Range(0, incoming.Length).Where(i => incoming[i] > 0).ToArray();
        // These can include downstream steps, not just the members of the cycle itself.
        return "Plan contains a dependency cycle; steps blocked by it: "
            + string.Join(", ", blocked.Take(20).Select(i => i + 1))
            + (blocked.Length > 20 ? ", …" : "") + ".";
    }
}
