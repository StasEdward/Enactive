namespace Enactive.Application;

using Enactive.Agents;
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
    /// <summary>
    /// Nobody answered in time: the run is told to stop at the question and keep it (DecisionPendingException - the
    /// engine writes it down and ends the run as waiting for somebody), unless an answer or a cancel won first.
    /// </summary>
    public bool Park(DecisionRequest request)
    {
        lock (_gate) return _source.TrySetException(new DecisionPendingException(request));
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
