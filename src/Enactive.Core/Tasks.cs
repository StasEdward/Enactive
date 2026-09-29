namespace Enactive.Core.Tasks;

/// <summary>The orchestrator's routing decision for an intent (PLAN_v2 §2.1).</summary>
public enum IntentDisposition { QuickAction, Task }

/// <summary>Status of a single plan step.</summary>
// Blocked (Phase 7): stopped at something the run cannot remove itself. Last, so stored names and numbers keep
// their meaning; a resumed run gives it back as Pending - the cause may be gone, and it is the run's to find out.
public enum StepStatus { Pending, Ready, Running, Done, Skipped, Failed, Blocked }

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
    StepComplexity Complexity = StepComplexity.Normal)
{
    public IReadOnlyList<string>? ObligationIds { get; init; }

    /// <summary>What this step hands on as values, when the plan declared it (Phase 2). Null: prose, as before.</summary>
    public StepOutputSchema? Output { get; init; }

    /// <summary>
    /// A step to be done once for EACH item another step hands on (Phase 5.3). It is not run as it
    /// stands: when the source step has handed its list on, the engine creates one step per item (or
    /// per batch of items) from it, and this step becomes the point where their results join.
    /// </summary>
    public ForEachSource? ForEach { get; init; }

    /// <summary>The items a step created from a <see cref="ForEach"/> step is for.</summary>
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>The <see cref="ForEach"/> step this one was created from.</summary>
    public Guid? ExpandedFrom { get; init; }

    /// <summary>
    /// A <see cref="ForEach"/> step whose items have been given steps of their own: it runs no model,
    /// waits until every one of them has ended - however it ended - and hands their results on as one.
    /// </summary>
    public bool Joins { get; init; }

    /// <summary>Why a <see cref="ForEach"/> step's items were given no steps, when they were not (a limit nobody lifted).</summary>
    public string? NotExpanded { get; init; }

    /// <summary>
    /// The document the engine assembles from this step's items' results (a <see cref="ForEach"/> step
    /// only): reserved for the engine - no step changes it - and written by code from what was recorded.
    /// </summary>
    public string? Report { get; init; }

    /// <summary>
    /// A step whose effect on the build must be known before anything else runs (Phase 6.1): it runs with
    /// nothing beside it, and is validated the moment it ends. Declared by the planner, never guessed.
    /// </summary>
    public bool Critical { get; init; }

    /// <summary>
    /// A step that only looks - reads, searches, lists, analyses, runs checks to see where things stand - and
    /// changes no file (Phase 6.1). Declared by the planner; the engine refuses its file changes.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>Where a step's items come from: a list field of an earlier step's output, by its plan position (0-based).</summary>
public sealed record ForEachSource(int Step, string Field);

/// <summary>A plan is a graph of steps. v1 builds a linear chain via <see cref="LinearPlan"/>.</summary>
public sealed record Plan(Guid Id, IReadOnlyList<PlanStep> Steps);

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

/// <summary>
/// A planner-emitted step: a title plus the 0-based indices of prerequisite steps.
/// <see cref="DependenciesDeclared"/> separates "the planner said this step depends on nothing" (an
/// explicit empty dependsOn - a genuinely independent step) from "the planner never mentioned
/// dependencies at all" (a bare title string). Without that distinction a fully independent plan -
/// exactly the one worth running in parallel - is indistinguishable from a legacy list of titles.
/// </summary>
public sealed record PlanStepSpec(
    string Title, IReadOnlyList<int> DependsOn, StepComplexity Complexity = StepComplexity.Normal,
    bool DependenciesDeclared = false)
{
    public IReadOnlyList<string>? ObligationIds { get; init; }
    public StepOutputSchema? Output { get; init; }
    public ForEachSource? ForEach { get; init; }
    public string? Report { get; init; }
    public bool Critical { get; init; }
    public bool ReadOnly { get; init; }
}

/// <summary>
/// Builds a real dependency graph from planner specs (index deps → step ids). Only when NO spec says
/// anything about dependencies does it fall back to a linear chain, so a legacy plain list of titles
/// behaves exactly as before while an explicit "dependsOn": [] stays independent.
/// Invalid and duplicate indices are dropped defensively. Self-dependencies are preserved for cycle validation.
/// </summary>
public static class DagPlan
{
    public static Plan FromSpecs(IReadOnlyList<PlanStepSpec> specs)
    {
        var ids = new Guid[specs.Count];
        for (var i = 0; i < specs.Count; i++)
            ids[i] = Guid.NewGuid();

        var graph = specs.Any(s => s.DependenciesDeclared || s.DependsOn.Count > 0);
        var steps = new List<PlanStep>(specs.Count);
        for (var i = 0; i < specs.Count; i++)
        {
            IReadOnlyList<Guid> deps;
            if (graph)
                deps = specs[i].DependsOn
                    .Where(d => d >= 0 && d < specs.Count)
                    .Distinct()
                    .Select(d => ids[d])
                    .ToArray();
            else
                deps = i > 0 ? new[] { ids[i - 1] } : Array.Empty<Guid>();

            steps.Add(new PlanStep(ids[i], specs[i].Title, StepStatus.Pending, deps, specs[i].Complexity)
                { ObligationIds = specs[i].ObligationIds, Output = specs[i].Output, ForEach = specs[i].ForEach, Report = specs[i].Report,
                  Critical = specs[i].Critical, ReadOnly = specs[i].ReadOnly });
        }
        return new Plan(Guid.NewGuid(), steps);
    }
}
