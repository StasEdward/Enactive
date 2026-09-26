namespace Enactive.Workspace;

// Single consumer; a pending snapshot replaces older pending snapshots, never an active write.
internal sealed class CoalescingWriter<T>(Action<T> write, TimeSpan delay) where T : class
{
    private readonly object _gate = new();
    private T? _pending;
    private Task? _worker;

    public void Queue(T snapshot)
    {
        lock (_gate)
        {
            _pending = snapshot;
            _worker ??= Task.Run(DrainAsync);
        }
    }

    public Task FlushAsync() { lock (_gate) return _worker ?? Task.CompletedTask; }

    private async Task DrainAsync()
    {
        while (true)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            T snapshot;
            lock (_gate) { snapshot = _pending!; _pending = null; }
            // Persistence here is best effort, matching WorkspaceRegistry's existing contract.
            try { write(snapshot); } catch { }
            lock (_gate)
            {
                if (_pending is not null) continue;
                _worker = null;
                return;
            }
        }
    }
}
