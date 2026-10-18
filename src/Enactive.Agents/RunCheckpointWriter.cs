namespace Enactive.Agents;

using Enactive.Core.History;

/// <summary>Serializes capture plus persistence, so a slower save cannot replace a newer snapshot.
/// Scheduling the boundaries and deciding whether an ending is resumable remain with the DAG.</summary>
internal sealed class RunCheckpointWriter(IRunCheckpointStore? store, Func<string, ValueTask> reportError) : IDisposable
{
    public RunCheckpointWriter(IRunCheckpointStore? store, Action<string> reportError)
        : this(store, message => { reportError(message); return ValueTask.CompletedTask; }) { }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _forgotten;

    internal async Task SaveAsync(Func<RunCheckpoint> capture)
    {
        if (store is null) return;
        await _gate.WaitAsync(CancellationToken.None);
        try { if (!_forgotten) await store.SaveAsync(capture(), CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { await reportError("Checkpoint could not be saved: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Saves, and says whether it did: null once the checkpoint is stored, otherwise why not. For a boundary somebody is
    /// promised they can carry on from (a pause, Plan Phase 4.2) - <see cref="SaveAsync"/> reports a failed save as a
    /// warning and the run goes on, which is right on the way and wrong at an end that claims to be resumable.
    /// </summary>
    internal async Task<string?> SaveOrSayWhyNotAsync(Func<RunCheckpoint> capture)
    {
        if (store is null) return "this run keeps no checkpoints";
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_forgotten) return "the run's checkpoint was already forgotten";
            await store.SaveAsync(capture(), CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ex.Message; }
        finally { _gate.Release(); }
    }

    /// <summary>Forgets one checkpoint and keeps writing this run's: the one a resumed run began from, once a newer one stands for it.</summary>
    internal async Task ForgetOnlyAsync(Guid runId)
    {
        if (store is null) return;
        await _gate.WaitAsync(CancellationToken.None);
        try { await DeleteAsync(runId); }
        finally { _gate.Release(); }
    }

    internal async Task ForgetAsync(Guid runId, Guid? resumedRunId)
    {
        if (store is null) return;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _forgotten = true; // A late queued save must not resurrect a completed run.
            await DeleteAsync(runId);
            if (resumedRunId is { } original && original != runId) await DeleteAsync(original);
        }
        finally { _gate.Release(); }
    }

    private async Task DeleteAsync(Guid id)
    {
        try { await store!.DeleteAsync(id, CancellationToken.None); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _gate.Dispose();
}
