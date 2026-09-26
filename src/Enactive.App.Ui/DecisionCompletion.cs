namespace Enactive.App.Ui;

using Enactive.Core.Permissions;

/// <summary>Cancellation and remembered approval have one winner, even on different threads.</summary>
internal sealed class DecisionCompletion
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<DecisionOutcome> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<DecisionOutcome> Task => _source.Task;
    public void Cancel(CancellationToken ct)
    {
        lock (_gate) _source.TrySetCanceled(ct);
    }
    public bool Resolve(string optionId, Action? remember = null)
    {
        lock (_gate)
        {
            if (_source.Task.IsCompleted) return false;
            try
            {
                remember?.Invoke();
                return _source.TrySetResult(new DecisionOutcome(optionId));
            }
            catch (Exception ex)
            {
                _source.TrySetException(ex);
                return false;
            }
        }
    }
}
