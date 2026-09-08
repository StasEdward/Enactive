namespace Enactive.Remote.Host;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Remote.Contracts;

/// <summary>
/// Lets a permission be answered from the phone, without taking that answer away from the desktop.
///
/// <para><b>It wraps the local handler rather than replacing it.</b> Both are asked, the first
/// answer wins, and the loser is cancelled. That is what makes "a queued remote answer loses to an
/// earlier local decision" true by construction instead of by a check somebody has to remember to
/// write - and it means sitting down at the computer always works, whatever the phone is doing.</para>
///
/// <para><b>A shell is never offered remotely.</b> The request is still published, so the panel can
/// say what is being asked and that the answer has to be given here; only the desktop is raced.
/// This is what keeps SANDBOX_PLAN.md's threat model standing now that starting a task has a
/// network origin: a leaked owner key must not become arbitrary command execution. The gateway
/// refuses such an answer as well - two independent refusals, because this one alone would be a
/// promise and that one alone would trust the panel.</para>
/// </summary>
/// <param name="remoteRunId">The run as the gateway knows it. A remote answer is about that id.</param>
/// <param name="timeout">
/// How long a request may go unanswered before it is treated as expired and denied. Shorter than
/// the gateway's own window on purpose: a step that waits for ever for an answer nobody can give is
/// a hang, not a permission model.
/// </param>
public sealed class RemoteDecisionHandler(
    IDecisionHandler desktop,
    HostStore store,
    RemoteApprovals approvals,
    string remoteRunId,
    TimeSpan timeout) : IDecisionHandler
{
    /// <summary>Two hours. Long enough to answer from a phone, short enough not to be a hang.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        // Without a bound action there is nothing to identify remotely, so this is a local question.
        // Publishing it would produce a card on the phone that no answer could ever be matched to.
        if (request.Action is not { } action)
        {
            return await desktop.RequestAsync(request, ct);
        }

        var approvalId = request.Id.ToString("N");
        var remotelyDecidable = !ShellTools.IsShell(action.Tool);

        var actionHash = ActionIdentity.Hash(
            remoteRunId, action.ToolCallId, action.Tool, action.WorkingDirectory, action.ArgumentsJson);

        store.Enqueue(remoteRunId, RemoteEventKind.ApprovalRequested, request.Topic,
            approval: new ApprovalRequest(
                approvalId, action.ToolCallId, action.Tool, request.FullText,
                action.WorkingDirectory, actionHash, remotelyDecidable));

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(timeout);

        var local = desktop.RequestAsync(request, race.Token);
        var remote = remotelyDecidable
            ? approvals.WaitAsync(approvalId, actionHash)
            : NeverAsync(race.Token);

        try
        {
            var winner = await Task.WhenAny(local, remote);

            // Whoever lost is told to stop: the desktop card closes, and a command arriving for
            // this request afterwards finds nothing waiting.
            await race.CancelAsync();
            approvals.Forget(approvalId);

            var outcome = winner == local
                ? await local
                : Answer(request, await remote);

            Report(approvalId, actionHash, Allowed(request, outcome) ? ApprovalOutcome.Allowed : ApprovalOutcome.Denied);
            return outcome;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The clock ran out, not the run. Reported as expired and answered as a refusal: the
            // safe answer to a question nobody answered is no.
            approvals.Forget(approvalId);
            Report(approvalId, actionHash, ApprovalOutcome.Expired);

            return Refuse(request);
        }
        catch (OperationCanceledException)
        {
            // The run itself was cancelled, so the request is not going to be answered by anyone.
            approvals.Forget(approvalId);
            Report(approvalId, actionHash, ApprovalOutcome.Invalidated);
            throw;
        }
    }

    private void Report(string approvalId, string actionHash, ApprovalOutcome outcome)
        => store.Enqueue(remoteRunId, RemoteEventKind.ApprovalResolved, outcome.ToString(),
            resolution: new ApprovalResolution(approvalId, actionHash, outcome));

    /// <summary>
    /// A remote allow or deny, expressed as one of the options this request actually offers.
    ///
    /// <para>The remote side only ever says allow or deny; the local card may offer more, and the
    /// options are the request's own. An allow takes the recommended option when there is one -
    /// which is the same thing the desktop's default button does.</para>
    /// </summary>
    private static DecisionOutcome Answer(DecisionRequest request, RemoteDecision decision)
        => decision == RemoteDecision.Allow
            ? new DecisionOutcome(
                request.RecommendedOptionId ?? request.Options[0].Id, "answered from the web")
            : Refuse(request, "denied from the web");

    private static DecisionOutcome Refuse(DecisionRequest request, string? because = null)
    {
        // A deny option if the request offers one, and otherwise the last option, which is the
        // convention these cards are built with. Never the recommended one: refusing by choosing
        // what was recommended is the one mistake here that cannot be noticed afterwards.
        var refusal = request.Options.LastOrDefault(o =>
            o.Id.Contains("deny", StringComparison.OrdinalIgnoreCase)
            || o.Id.Contains("no", StringComparison.OrdinalIgnoreCase)
            || o.Label.Contains("deny", StringComparison.OrdinalIgnoreCase));

        return new DecisionOutcome(
            (refusal ?? request.Options[^1]).Id,
            because ?? "nobody answered before the request expired");
    }

    private static bool Allowed(DecisionRequest request, DecisionOutcome outcome)
        => !string.Equals(outcome.OptionId, Refuse(request).OptionId, StringComparison.Ordinal);

    /// <summary>A task that only ever ends by cancellation - the remote side of a shell request.</summary>
    private static async Task<RemoteDecision> NeverAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return RemoteDecision.Deny;
    }
}
