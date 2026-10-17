namespace Enactive.App.Ui;

using Enactive.Core.Execution;

/// <summary>The foreground run owns Stop - and its pause - until its cleanup has completed.</summary>
internal sealed class ForegroundRunSlot
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private RunPause? _pause;
    private TaskCompletionSource? _finished;
    private bool _closing;

    /// <summary>
    /// The running foreground run's pause (Plan Phase 4.2), made with it beside the cancellation Stop uses; null when no
    /// run holds the slot. One per run: a pause already asked for, kept for the next run - a resume - would pause it
    /// before its first step.
    /// </summary>
    public RunPause? Pause
    {
        get { lock (_gate) return _pause; }
    }

    private string? _workspace;

    /// <summary>
    /// The folder the foreground run works in, while one holds the slot; null otherwise. The checks list refuses to change
    /// that folder's checks meanwhile: the run puts them back as they were when it started (Plan Phase 3.1, 3.5).
    /// </summary>
    public string? Workspace
    {
        get { lock (_gate) return _current is null ? null : _workspace; }
    }

    /// <param name="workspace">The folder the run works in, for <see cref="Workspace"/>.</param>
    public CancellationTokenSource? TryStart(string? workspace = null)
    {
        lock (_gate)
        {
            if (_closing || _current is not null) return null;
            _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pause = new RunPause();
            _workspace = workspace;
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
            _pause = null;
            _workspace = null;
            _finished?.TrySetResult();
            return true;
        }
    }
}
