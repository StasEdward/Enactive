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

    /// <summary>
    /// A scheduler picking up where an interrupted run left off.
    ///
    /// <para><b>A step that was RUNNING comes back Pending.</b> Nobody is running it - the process
    /// that was is gone. Leaving it Running would be the worst of the three options - it would never be
    /// dispatched and never unblock anything, so the resumed run would stall against a step it was
    /// silently not doing. Calling it Done would be worse still: a step that got halfway is not a
    /// step that finished, and everything downstream would build on work that was never completed.
    /// Pending is the only status that is true, and its cost - the step is redone from its
    /// beginning - is the cost this whole feature is honest about.</para>
    ///
    /// <para>A step the checkpoint does not mention is Pending too. That is a plan and a checkpoint
    /// that disagree, which should not happen; doing such a step is recoverable and skipping it is
    /// not.</para>
    ///
    /// <para>The FAILURE CASCADE is then replayed. A live run cascade-skips a failed step's
    /// dependents the moment it fails, but that happens inside <see cref="MarkFailed"/> and a
    /// restored run never calls it. Without replaying it here, a dependent of a step that had
    /// already failed sits Pending and can never become ready - which the run then reports as
    /// "unresolvable dependencies (a cycle)", a diagnosis that is simply wrong. The plan is fine;
    /// something it depended on did not work.</para>
    /// </summary>
    public DagScheduler(Plan plan, IReadOnlyDictionary<Guid, StepStatus> restore)
    {
        _steps = plan.Steps;
        foreach (var s in _steps)
        {
            var status = restore.TryGetValue(s.Id, out var stored) ? stored : StepStatus.Pending;
            _status[s.Id] = status is StepStatus.Running or StepStatus.Ready ? StepStatus.Pending : status;
        }

        CascadeSkips();
    }

    /// <summary>
    /// Skips every Pending step that (transitively) depends on one that failed or was skipped.
    /// Shared by <see cref="MarkFailed"/> and by the restoring constructor, so a resumed run reaches
    /// the same state a live one would have been in. Caller must hold the gate, or be a constructor.
    /// </summary>
    private List<PlanStep> CascadeSkips()
    {
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
                    if (_status.TryGetValue(dep, out var st) && st is StepStatus.Failed or StepStatus.Skipped)
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

    /// <summary>
    /// Every step's status right now, for writing down. A copy taken under the gate: the caller is
    /// on a step's own thread while other steps are still finishing, and handing out the live
    /// dictionary would let a checkpoint be read while it is being changed.
    /// </summary>
    public IReadOnlyDictionary<Guid, StepStatus> Snapshot()
    {
        lock (_gate)
            return new Dictionary<Guid, StepStatus>(_status);
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
        lock (_gate)
        {
            if (_status.ContainsKey(id))
                _status[id] = StepStatus.Failed;

            // Only Pending steps are skipped. A dependent that is already Running cannot be recalled;
            // it finishes on its own and is recorded normally.
            return CascadeSkips();
        }
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
