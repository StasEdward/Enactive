namespace Enactive.App.Ui;

/// <summary>The foreground run owns Stop until its cleanup has completed.</summary>
internal sealed class ForegroundRunSlot
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private TaskCompletionSource? _finished;
    private bool _closing;
    public CancellationTokenSource? TryStart()
    {
        lock (_gate)
        {
            if (_closing || _current is not null) return null;
            _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _current = new CancellationTokenSource();
        }
    }
    public void Stop()
    {
        CancellationTokenSource? owner;
        lock (_gate) owner = _current;
        _ = CancelAsync(owner);
    }
    public Task StopAsync()
    {
        CancellationTokenSource? owner;
        Task finished;
        lock (_gate)
        {
            _closing = true;
            owner = _current;
            finished = _finished?.Task ?? Task.CompletedTask;
        }
        return Task.WhenAll(CancelAsync(owner), finished);
    }

    private static async Task CancelAsync(CancellationTokenSource? owner)
    {
        if (owner is null) return;
        try { await owner.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* The captured owner finished before Stop reached it. */ }
        catch (Exception ex)
        {
            // A callback must not crash the UI or skip the owning run's cleanup.
            System.Diagnostics.Trace.TraceError("Run cancellation callback failed: {0}", ex);
        }
    }
    public bool Finish(CancellationTokenSource owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, owner)) return false;
            _current = null;
            _finished?.TrySetResult();
            return true;
        }
    }
}
