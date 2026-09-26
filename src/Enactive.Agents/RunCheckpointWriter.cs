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
