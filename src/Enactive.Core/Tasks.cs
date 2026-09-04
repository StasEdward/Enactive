namespace Enactive.Core.Tasks;

using Enactive.Core.Intents;

/// <summary>The orchestrator's routing decision for an intent (PLAN_v2 §2.1).</summary>
public enum IntentDisposition { QuickAction, Task }

/// <summary>Lifecycle of a task.</summary>
public enum WorkStatus { New, Understanding, Planning, Executing, WaitingForUser, Completed, Failed, Cancelled }

/// <summary>Status of a single plan step.</summary>
public enum StepStatus { Pending, Ready, Running, Done, Skipped, Failed }

/// <summary>
/// How hard a step is, as judged by the planner. Drives per-step model auto-routing: Trivial steps can run
/// on a smaller/faster model, Complex steps on a stronger one, Normal on the worker's own model.
/// </summary>
public enum StepComplexity { Trivial, Normal, Complex }

/// <summary>
/// One step of a plan. Carries <see cref="DependsOn"/> so a linear chain (v1) and a future DAG share
/// the same type (PLAN_v2 §2.4).
/// </summary>
public sealed record PlanStep(
    Guid Id, string Title, StepStatus Status, IReadOnlyList<Guid> DependsOn,
    StepComplexity Complexity = StepComplexity.Normal);

/// <summary>A plan is a graph of steps. v1 builds a linear chain via <see cref="LinearPlan"/>.</summary>
public sealed record Plan(Guid Id, IReadOnlyList<PlanStep> Steps);

/// <summary>A task: the primary unit of multi-step work. Flat, with an optional plan.</summary>
public sealed record AgentTask(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    Intent Origin,
    WorkStatus Status,
    Plan? Plan,
    string WorkerId);

/// <summary>The only place that assumes linearity — building a DAG later is isolated here.</summary>
public static class LinearPlan
{
    public static Plan FromTitles(IEnumerable<string> titles)
    {
        var steps = new List<PlanStep>();
        Guid? previous = null;
        foreach (var title in titles)
        {
            var id = Guid.NewGuid();
            var dependsOn = previous is { } p ? new[] { p } : Array.Empty<Guid>();
            steps.Add(new PlanStep(id, title, StepStatus.Pending, dependsOn));
            previous = id;
        }
        return new Plan(Guid.NewGuid(), steps);
    }
}

/// <summary>A planner-emitted step: a title plus the 0-based indices of prerequisite steps.</summary>
public sealed record PlanStepSpec(
    string Title, IReadOnlyList<int> DependsOn, StepComplexity Complexity = StepComplexity.Normal);

/// <summary>
/// Builds a real dependency graph from planner specs (index deps → step ids). If no spec declares any
/// dependency, it falls back to a linear chain so a plain list of steps behaves exactly as before.
/// Invalid, self- and duplicate indices are dropped defensively.
/// </summary>
public static class DagPlan
{
    public static Plan FromSpecs(IReadOnlyList<PlanStepSpec> specs)
    {
        var ids = new Guid[specs.Count];
        for (var i = 0; i < specs.Count; i++)
            ids[i] = Guid.NewGuid();

        var anyDeps = specs.Any(s => s.DependsOn.Count > 0);
        var steps = new List<PlanStep>(specs.Count);
        for (var i = 0; i < specs.Count; i++)
        {
            IReadOnlyList<Guid> deps;
            if (anyDeps)
                deps = specs[i].DependsOn
                    .Where(d => d >= 0 && d < specs.Count && d != i)
                    .Distinct()
                    .Select(d => ids[d])
                    .ToArray();
            else
                deps = i > 0 ? new[] { ids[i - 1] } : Array.Empty<Guid>();

            steps.Add(new PlanStep(ids[i], specs[i].Title, StepStatus.Pending, deps, specs[i].Complexity));
        }
        return new Plan(Guid.NewGuid(), steps);
    }
}
