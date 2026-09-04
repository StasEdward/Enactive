namespace Enactive.Agents;

using Enactive.Core.Tasks;

/// <summary>
/// Executes a <see cref="Plan"/> as a real dependency graph, not a fixed sequence: a step becomes
/// runnable only once all of its dependencies are Done. Sequential (one ready step at a time) to keep
/// the shared conversation coherent, but driven by readiness — so diamonds, multiple roots and
/// branches all run in a valid order. On a failure it cascade-skips every dependent; a plan whose
/// remaining steps can never become ready (a cycle) is detected via <see cref="HasPending"/>.
/// </summary>
public sealed class DagScheduler
{
    private readonly IReadOnlyList<PlanStep> _steps;
    private readonly Dictionary<Guid, StepStatus> _status = new();

    public DagScheduler(Plan plan)
    {
        _steps = plan.Steps;
        foreach (var s in _steps)
            _status[s.Id] = StepStatus.Pending;
    }

    public int Total => _steps.Count;
    public int DoneCount => _steps.Count(s => _status[s.Id] == StepStatus.Done);
    public bool HasPending => _steps.Any(s => _status[s.Id] == StepStatus.Pending);

    /// <summary>The next Pending step whose dependencies are all Done, or null if none is ready now.</summary>
    public PlanStep? NextReady()
    {
        foreach (var s in _steps)
        {
            if (_status[s.Id] != StepStatus.Pending)
                continue;
            var ready = true;
            foreach (var dep in s.DependsOn)
                if (_status.TryGetValue(dep, out var st) && st != StepStatus.Done)
                {
                    ready = false;
                    break;
                }
            if (ready)
                return s;
        }
        return null;
    }

    public void MarkDone(Guid id)
    {
        if (_status.ContainsKey(id))
            _status[id] = StepStatus.Done;
    }

    /// <summary>Marks the step failed and cascade-skips every step that (transitively) depends on it.</summary>
    public IReadOnlyList<PlanStep> MarkFailed(Guid id)
    {
        if (_status.ContainsKey(id))
            _status[id] = StepStatus.Failed;

        var skipped = new List<PlanStep>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in _steps)
            {
                if (_status[s.Id] != StepStatus.Pending)
                    continue;
                foreach (var dep in s.DependsOn)
                    if (_status.TryGetValue(dep, out var st) && (st == StepStatus.Failed || st == StepStatus.Skipped))
                    {
                        _status[s.Id] = StepStatus.Skipped;
                        skipped.Add(s);
                        changed = true;
                        break;
                    }
            }
        }
        return skipped;
    }
}
