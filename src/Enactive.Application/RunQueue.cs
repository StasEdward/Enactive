namespace Enactive.Application;

using Enactive.Workspace;

/// <summary>
/// Runs waiting to start on a host that serves several people: at most <see cref="MaxActive"/> at once, and one per
/// workspace (server plan A3).
///
/// <para><b>Why a queue, and not a refusal.</b> Two people on the service will start tasks in the same folder, and
/// "busy, try again" makes each of them poll. A run that cannot start yet waits its turn and can say why
/// (<see cref="Ticket.Waiting"/>) - which is the only thing the person who started it needs to know.</para>
///
/// <para><b>Order.</b> First come, first served - except that a run for a free folder is not held behind one waiting for
/// a busy folder: the earliest waiter that CAN start does. Strict first-in-first-out would leave every slot idle behind
/// one task waiting for a folder somebody else is using.</para>
///
/// <para><b>Across processes</b> the workspace is kept by its lease (WorkspaceLease), taken when the run is composed.
/// This queue orders the runs of one host; it does not replace that.</para>
///
/// <para>The desktop does not use it: there one foreground run and the background group are the person's own, and a
/// folder may have both. It is what the service shares among its people.</para>
/// </summary>
internal sealed class RunQueue
{
    private readonly object _gate = new();
    private readonly List<Ticket> _waiting = new();
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private int _active;

    public RunQueue(int maxActive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxActive, 1);
        MaxActive = maxActive;
    }

    /// <summary>How many runs may be going at once. Each run's own parallel steps are its own (MaxParallelSteps).</summary>
    public int MaxActive { get; }

    public int Active
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>Puts a run for this workspace in line. Disposing the ticket leaves the line, or frees the slot.</summary>
    public Ticket Enqueue(string workspace)
    {
        var ticket = new Ticket(this, WorkspaceRegistry.Normalise(workspace));
        lock (_gate)
        {
            _waiting.Add(ticket);
            Pump();
        }
        return ticket;
    }

    /// <summary>Starts every waiter that can start, earliest first. Called under the gate.</summary>
    private void Pump()
    {
        for (var i = 0; i < _waiting.Count && _active < MaxActive;)
        {
            var next = _waiting[i];
            if (_busy.Contains(next.Workspace))
            {
                i++;
                continue;
            }

            _waiting.RemoveAt(i);
            _busy.Add(next.Workspace);
            _active++;
            next.Start();
        }
    }

    /// <summary>Called under the gate, in the same hold that marked the ticket gone - see Ticket.Dispose.</summary>
    private void Leave(Ticket ticket, bool started)
    {
        if (started)
        {
            _busy.Remove(ticket.Workspace);
            _active--;
        }
        else
        {
            _waiting.Remove(ticket);
        }
        Pump();
    }

    /// <summary>Why this ticket is still waiting, or null when it has started. Called under the gate.</summary>
    private string? WhyWaiting(Ticket ticket)
    {
        var at = _waiting.IndexOf(ticket);
        if (at < 0)
            return null;

        if (_busy.Contains(ticket.Workspace) || _waiting.Take(at).Any(w => SameFolder(w, ticket)))
            return "Another run in this folder is going; this one starts when it ends.";

        var ahead = _waiting.Take(at).Count(w => !_busy.Contains(w.Workspace));
        return $"All {MaxActive} run slots are taken; this one starts when one is free"
               + (ahead > 0 ? $" ({ahead} ahead of it)." : ".");
    }

    private static bool SameFolder(Ticket a, Ticket b)
        => string.Equals(a.Workspace, b.Workspace, StringComparison.OrdinalIgnoreCase);

    /// <summary>One run's place: waiting, then started, then let go.</summary>
    public sealed class Ticket : IDisposable
    {
        private readonly RunQueue _queue;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;   // 0 waiting, 1 started, 2 left

        internal Ticket(RunQueue queue, string workspace)
        {
            _queue = queue;
            Workspace = workspace;
        }

        public string Workspace { get; }

        /// <summary>Completes when the run may start; cancelled when the ticket is disposed before that.</summary>
        public Task Started => _started.Task;

        /// <summary>Why the run has not started, in words for the person who started it; null once it has.</summary>
        public string? Waiting
        {
            get { lock (_queue._gate) return _queue.WhyWaiting(this); }
        }

        internal void Start()
        {
            _state = 1;
            _started.TrySetResult();
        }

        public void Dispose()
        {
            // One hold of the gate for "gone" and for leaving the line: let go between the two, and the queue could
            // start this ticket in the gap - a slot and a folder taken by a run that has already left.
            lock (_queue._gate)
            {
                if (_state == 2)
                    return;
                var started = _state == 1;
                _state = 2;
                _queue.Leave(this, started);
            }
            _started.TrySetCanceled();
        }
    }
}
