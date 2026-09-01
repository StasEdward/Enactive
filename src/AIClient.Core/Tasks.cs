namespace AIClient.Core.Tasks;

using AIClient.Core.Intents;

/// <summary>The orchestrator's routing decision for an intent (PLAN_v2 §2.1).</summary>
public enum IntentDisposition { QuickAction, Task }

/// <summary>Lifecycle of a task.</summary>
public enum WorkStatus { New, Understanding, Planning, Executing, WaitingForUser, Completed, Failed, Cancelled }

/// <summary>Status of a single plan step.</summary>
public enum StepStatus { Pending, Ready, Running, Done, Skipped, Failed }

/// <summary>
/// One step of a plan. Carries <see cref="DependsOn"/> so a linear chain (v1) and a future DAG share
/// the same type (PLAN_v2 §2.4).
/// </summary>
public sealed record PlanStep(Guid Id, string Title, StepStatus Status, IReadOnlyList<Guid> DependsOn);

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
