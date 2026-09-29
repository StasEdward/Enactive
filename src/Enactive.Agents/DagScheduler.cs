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
    // Grows while the run does (Phase 5.3): a step to be done for each item is given one step per
    // item once the item list exists. New steps go at the END, so every step keeps its plan position.
    private readonly List<PlanStep> _steps;
    private readonly Dictionary<Guid, StepStatus> _status = new();
    private readonly object _gate = new();

    public DagScheduler(Plan plan)
    {
        _steps = plan.Steps.ToList();
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
        _steps = plan.Steps.ToList();
        foreach (var s in _steps)
        {
            var status = restore.TryGetValue(s.Id, out var stored) ? stored : StepStatus.Pending;
            // A BLOCKED step comes back Pending too (Phase 7): what stopped it may have been put right, and a
            // resumed run finds out by doing it again - it is blocked again, and says so, if not.
            _status[s.Id] = status is StepStatus.Running or StepStatus.Ready or StepStatus.Blocked ? StepStatus.Pending : status;
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
                if (JoinCannotRun(s) ?? s.DependsOn.Any(dep => _status.TryGetValue(dep, out var st) && st is StepStatus.Failed or StepStatus.Skipped))
                {
                    _status[s.Id] = StepStatus.Skipped;
                    skipped.Add(s);
                    changed = true;
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

    public int Total
    {
        get { lock (_gate) return _steps.Count; }
    }

    /// <summary>Every step as it stands now - with the steps the run has grown, in plan order.</summary>
    public IReadOnlyList<PlanStep> Steps
    {
        get { lock (_gate) return _steps.ToArray(); }
    }

    /// <summary>
    /// Gives a step to be done for each item its steps (Phase 5.3): they are added at the end of the
    /// plan, Pending, and the step itself - handed out once its source was Done, to do exactly this -
    /// goes back to Pending as the join that waits for them. With none - no items,
    /// or a limit nobody lifted - the join is ready at once and says why, rather than waiting forever.
    /// </summary>
    public void Expand(Guid forEachId, IReadOnlyList<PlanStep> expansions, string? notExpanded = null)
    {
        lock (_gate)
        {
            var at = _steps.FindIndex(s => s.Id == forEachId);
            if (at < 0) throw new InvalidOperationException("No such step to expand.");
            var step = _steps[at];
            if (step.Joins) throw new InvalidOperationException($"'{step.Title}' has already been expanded.");
            _steps[at] = step with { DependsOn = [.. step.DependsOn, .. expansions.Select(e => e.Id)], Joins = true, NotExpanded = notExpanded };
            // It was handed out to be expanded; now it waits, as the join, for what it was given.
            _status[forEachId] = StepStatus.Pending;
            foreach (var expansion in expansions)
            {
                _steps.Add(expansion with { ExpandedFrom = forEachId });
                _status[expansion.Id] = StepStatus.Pending;
            }
        }
    }

    /// <summary>
    /// Whether a join can never run: something it waits on OUTSIDE its items failed. Null for a step
    /// that is not a join. Items that failed do not hold the join back: nine of twelve is "these nine,
    /// not these three", never nothing (amendment D).
    /// </summary>
    private bool? JoinCannotRun(PlanStep step)
    {
        if (!step.Joins) return null;
        // Not even when none of them succeeded: the join still reports what each came to, and so can
        // what follows it - "one page not finished, eleven not reached" is a result, and a skipped
        // report is none (run 80c951, 2026-09-28: the final report was skipped behind twelve failed pages).
        return step.DependsOn.Where(d => !IsItemOf(step)(d)).Any(d => _status.TryGetValue(d, out var st) && st is StepStatus.Failed or StepStatus.Skipped);
    }

    private Func<Guid, bool> IsItemOf(PlanStep join)
        => id => _steps.Any(s => s.Id == id && s.ExpandedFrom == join.Id);

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
    /// <param name="running">How many steps are in flight. A <see cref="PlanStep.Critical"/> step is handed
    /// out only when none is, and alone; while one is ready or running nothing else is (Phase 6).</param>
    public IReadOnlyList<PlanStep> NextReadyBatch(int max, int running = 0)
    {
        if (max <= 0)
            return Array.Empty<PlanStep>();

        var ready = new List<PlanStep>();
        lock (_gate)
        {
            if (_steps.Any(s => s.Critical && _status[s.Id] == StepStatus.Running))
                return Array.Empty<PlanStep>();
            // A critical step that is ready waits for what is running to finish - and nothing new starts
            // meanwhile, or a steady stream of other steps could keep it waiting for ever.
            if (_steps.FirstOrDefault(s => s.Critical && _status[s.Id] == StepStatus.Pending && DependenciesSatisfied(s)) is { } critical)
            {
                if (running > 0)
                    return Array.Empty<PlanStep>();
                _status[critical.Id] = StepStatus.Running;
                return [critical];
            }

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

    /// <summary>
    /// Marks the step blocked (Phase 7) and blocks every Pending step that (transitively) waits on it - not
    /// skipped: they have not failed and nothing they need has, and a resumed run does them. A step that also
    /// waits on one that FAILED is skipped as before; that does not come back.
    /// </summary>
    public IReadOnlyList<PlanStep> MarkBlocked(Guid id)
    {
        lock (_gate)
        {
            if (_status.ContainsKey(id))
                _status[id] = StepStatus.Blocked;
            CascadeSkips();
            var blocked = new List<PlanStep>();
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var s in _steps)
                {
                    if (_status[s.Id] != StepStatus.Pending
                        || !s.DependsOn.Any(dep => _status.TryGetValue(dep, out var st) && st == StepStatus.Blocked))
                        continue;
                    _status[s.Id] = StepStatus.Blocked;
                    blocked.Add(s);
                    changed = true;
                }
            }
            return blocked;
        }
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

    /// <summary>
    /// Whether every step this one waits on has finished successfully. Caller must hold the gate.
    ///
    /// <para>A dependency this plan does not CONTAIN is not satisfied either, and that used to be
    /// the other way round: the lookup and the status test were one <c>&amp;&amp;</c>, so a
    /// dependency the scheduler had never heard of short-circuited to "fine" and the step ran
    /// immediately. An absence is not an answer, and the absent thing here is the entire reason to
    /// wait.</para>
    ///
    /// <para>Not reachable from a planner's output - <c>DagPlan.FromSpecs</c> drops any index that
    /// is not a real step - but very reachable from a plan rebuilt out of STORED data:
    /// <c>Orchestrator.PlanOf</c> takes a checkpoint's dependency ids as given, and a checkpoint
    /// written by an older build, hand-edited or truncated can name a step that is not in the list
    /// beside it. A step running before its prerequisite is the worst failure this class has.</para>
    /// </summary>
    private bool DependenciesSatisfied(PlanStep step)
    {
        var item = step.Joins ? IsItemOf(step) : _ => false;
        foreach (var dep in step.DependsOn)
        {
            if (!_status.TryGetValue(dep, out var st)) return false;
            // A join waits for its items to END, not to succeed; the others must be Done.
            if (st != StepStatus.Done && !(item(dep) && st is StepStatus.Failed or StepStatus.Skipped))
                return false;
        }
        return true;
    }
}
