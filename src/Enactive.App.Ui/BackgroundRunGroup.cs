namespace Enactive.App.Ui;

/// <summary>Owns local background work through recording, resource disposal and UI cleanup.</summary>
internal sealed class BackgroundRunGroup
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<Task> _running = [];
    private Task? _shutdown;

    public int Count { get { lock (_gate) return _running.Count; } }

    public Task? TryStart(Func<CancellationToken, Task> run)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_shutdown is not null) return null;
            _running.Add(completion.Task);
        }
        // Never pass the cancelled token to Task.Run: the delegate must execute its cleanup even
        // when shutdown wins the race before composition begins.
        _ = Task.Run(async () =>
        {
            try
            {
                await run(_cancellation.Token).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { lock (_gate) _running.Remove(completion.Task); }
        });
        return completion.Task;
    }

    public Task StopAsync()
    {
        Task[] runs;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdown is not null) return _shutdown;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdown = completion.Task;
            runs = _running.ToArray();
        }
        // Admission is closed; cancellation callbacks run outside the ownership lock.
        _ = DrainAsync(runs, completion);
        return completion.Task;
    }

    private async Task DrainAsync(Task[] runs, TaskCompletionSource completion)
    {
        try
        {
            // Await every run even if a cancellation callback throws.
            await Task.WhenAll(_cancellation.CancelAsync(), Task.WhenAll(runs)).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally { _cancellation.Dispose(); }
    }
}
