namespace Enactive.Agents;

using Enactive.Core.Tasks;

/// <summary>
/// Executes a <see cref="Plan"/> as a real dependency graph, not a fixed sequence: a step becomes
/// runnable only once all of its dependencies are Done. <see cref="NextReadyBatch"/> can hand out
/// several ready steps at once, so independent branches of a diamond run concurrently; the caller
/// decides how many it is willing to run at a time. On a failure it cascade-skips every dependent;
/// a plan whose remaining steps can never become ready (a cycle) is detected via <see cref="HasPending"/>.
///
/// Thread-safe: with parallel execution, MarkDone/MarkFailed arrive from several step tasks at once.
/// A step handed out by NextReadyBatch is immediately marked Running, so it is never dispatched twice.
/// </summary>
public sealed class DagScheduler
{
    private readonly IReadOnlyList<PlanStep> _steps;
    private readonly Dictionary<Guid, StepStatus> _status = new();
    private readonly object _gate = new();

    public DagScheduler(Plan plan)
    {
        _steps = plan.Steps;
        foreach (var s in _steps)
            _status[s.Id] = StepStatus.Pending;
    }

    public int Total => _steps.Count;

    public int DoneCount
    {
        get { lock (_gate) return _steps.Count(s => _status[s.Id] == StepStatus.Done); }
    }

    /// <summary>True while at least one step is still waiting for its dependencies.</summary>
    public bool HasPending
    {
        get { lock (_gate) return _steps.Any(s => _status[s.Id] == StepStatus.Pending); }
    }

    /// <summary>The next ready step, marked Running, or null if none is ready now.</summary>
    public PlanStep? NextReady() => NextReadyBatch(1).FirstOrDefault();

    /// <summary>
    /// Up to <paramref name="max"/> Pending steps whose dependencies are all Done. Every returned step
    /// is marked Running before it is returned, so concurrent callers never receive the same step.
    /// </summary>
    public IReadOnlyList<PlanStep> NextReadyBatch(int max)
    {
        if (max <= 0)
            return Array.Empty<PlanStep>();

        var ready = new List<PlanStep>();
        lock (_gate)
        {
            foreach (var s in _steps)
            {
                if (ready.Count >= max)
                    break;
                if (_status[s.Id] != StepStatus.Pending)
                    continue;
                if (!DependenciesSatisfied(s))
                    continue;
                _status[s.Id] = StepStatus.Running;
                ready.Add(s);
            }
        }
        return ready;
    }

    public void MarkDone(Guid id)
    {
        lock (_gate)
        {
            if (_status.ContainsKey(id))
                _status[id] = StepStatus.Done;
        }
    }

    /// <summary>
    /// Gives up on everything still waiting, and says what it gave up on.
    ///
    /// <para>For a run that has hit a limit: the steps that never started are Skipped rather than
    /// left Pending, because a Pending step at the end of a run is indistinguishable from a
    /// scheduler bug - and because the run's own outcome is built from its steps', so a step with no
    /// recorded outcome would quietly not count. Steps already RUNNING are left alone: they cannot
    /// be recalled and they finish and record normally.</para>
    /// </summary>
    public IReadOnlyList<PlanStep> AbandonPending()
    {
        var abandoned = new List<PlanStep>();
        lock (_gate)
        {
            foreach (var s in _steps)
            {
                if (_status[s.Id] != StepStatus.Pending)
                    continue;
                _status[s.Id] = StepStatus.Skipped;
                abandoned.Add(s);
            }
        }
        return abandoned;
    }

    /// <summary>Marks the step failed and cascade-skips every step that (transitively) depends on it.</summary>
    public IReadOnlyList<PlanStep> MarkFailed(Guid id)
    {
        var skipped = new List<PlanStep>();
        lock (_gate)
        {
            if (_status.ContainsKey(id))
                _status[id] = StepStatus.Failed;

            // Only Pending steps are skipped. A dependent that is already Running cannot be recalled;
            // it finishes on its own and is recorded normally.
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
        }
        return skipped;
    }

    /// <summary>Caller must hold the gate.</summary>
    private bool DependenciesSatisfied(PlanStep step)
    {
        foreach (var dep in step.DependsOn)
            if (_status.TryGetValue(dep, out var st) && st != StepStatus.Done)
                return false;
        return true;
    }
}
