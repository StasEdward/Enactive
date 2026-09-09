namespace Enactive.Remote.Host;

using System.Collections.Concurrent;
using Enactive.Remote.Contracts;

/// <summary>
/// The requests this machine is currently waiting on an answer to, and the one place a queued
/// remote answer is checked before it is believed.
///
/// <para>Delivery of a ResolveApproval command is NOT authorisation. It may arrive after the same
/// request was answered on the desktop, after the request expired, or after the run ended - the
/// gateway cannot know any of that, because all three are facts about this machine. So every check
/// lives here, on the side that has the facts.</para>
/// </summary>
public sealed class RemoteApprovals
{
    private readonly ConcurrentDictionary<string, Waiting> _waiting = new();

    private sealed record Waiting(string ActionHash, TaskCompletionSource<RemoteDecision> Answer);

    /// <summary>Requests still open, for the desktop to show and for tests.</summary>
    public IReadOnlyCollection<string> Pending => _waiting.Keys.ToArray();

    /// <summary>
    /// Starts waiting for a remote answer to one request.
    ///
    /// <para>The hash is remembered with it, so an answer can be checked against the action it was
    /// actually asked about rather than merely against the request's name.</para>
    /// </summary>
    public Task<RemoteDecision> WaitAsync(string approvalId, string actionHash)
    {
        var waiting = new Waiting(actionHash,
            new TaskCompletionSource<RemoteDecision>(TaskCreationOptions.RunContinuationsAsynchronously));

        return _waiting.AddOrUpdate(approvalId, waiting, (_, existing) => existing).Answer.Task;
    }

    /// <summary>
    /// Delivers an answer that arrived from the gateway, if it is still one this machine is asking.
    ///
    /// <para>Returns false when the request is no longer open - which is the ordinary case, not an
    /// error: the desktop answered first. The caller reports that outcome rather than this one, and
    /// the gateway learns of it from the ApprovalResolved event either way.</para>
    /// </summary>
    public bool TryAnswer(string approvalId, string actionHash, RemoteDecision decision)
    {
        if (!_waiting.TryGetValue(approvalId, out var waiting))
        {
            return false;
        }

        // The answer has to be about the action the request was raised for. Compared, never
        // recomputed - the point of the hash is that both ends are talking about the same bytes.
        if (!string.Equals(waiting.ActionHash, actionHash, StringComparison.Ordinal))
        {
            return false;
        }

        return waiting.Answer.TrySetResult(decision) && _waiting.TryRemove(approvalId, out _);
    }

    /// <summary>
    /// Stops waiting. Called when something else settled the request - the desktop answered, the
    /// clock ran out, the run was cancelled - so a later command finds nothing to answer.
    /// </summary>
    public void Forget(string approvalId) => _waiting.TryRemove(approvalId, out _);
}
