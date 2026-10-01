namespace Enactive.Remote.Host;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;

/// <summary>
/// Lets a permission be answered from the phone, without taking that answer away from the desktop.
///
/// <para><b>It wraps the local handler rather than replacing it.</b> Both are asked, the first
/// answer wins, and the loser is cancelled. That is what makes "a queued remote answer loses to an
/// earlier local decision" true by construction instead of by a check somebody has to remember to
/// write - and it means sitting down at the computer always works, whatever the phone is doing.</para>
///
/// <para><b>A run started from the web never runs a shell.</b> Not "unless somebody happens to be
/// at the machine": the request is published so the panel can show what was asked, and then refused
/// at once, without the desktop being asked at all.</para>
///
/// <para>An earlier version raced the desktop for shells, on the reasoning that the person at the
/// keyboard should always be able to answer. That leaves a path where a leaked owner key plus one
/// casual click on a card somebody did not start becomes arbitrary command execution - and the
/// click is the easy half. The sandbox plan's threat model is justified by "the machine is the
/// developer's own, the projects are theirs"; a network origin is exactly what that argument does
/// not cover, so the rule is flat and has no exception to reason about. The gateway refuses such an
/// answer as well: two independent refusals, because this one alone would be a promise and that one
/// alone would trust the panel.</para>
///
/// <para>Refused AT ONCE rather than left to expire. The outcome is the same either way and the
/// step is dead the moment it is asked, so waiting two hours to say so buys nothing and costs the
/// owner an afternoon of watching a run that was never going to move.</para>
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
    Sealer sealer,
    string remoteRunId,
    TimeSpan timeout) : IDecisionHandler
{
    /// <summary>Two hours. Long enough to answer from a phone, short enough not to be a hang.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        request = request with { RequiresExplicitAnswer = true };
        // Without a bound action there is nothing to identify remotely, so this is a local question.
        // Publishing it would produce a card on the phone that no answer could ever be matched to.
        if (request.Action is not { } action)
        {
            var localAnswer = await desktop.RequestAsync(request, ct);
            ct.ThrowIfCancellationRequested();
            return localAnswer;
        }

        var approvalId = request.Id.ToString("N");
        var remotelyDecidable = !ShellTools.IsShell(action.Tool);

        var actionHash = ActionIdentity.Hash(
            remoteRunId, action.ToolCallId, action.Tool, action.WorkingDirectory, action.ArgumentsJson);

        // Sealed whole: the card shows FullText, the hash covers ArgumentsJson, and the browser needs
        // both - one to show the person, the other to recompute the hash and refuse an Allow for an
        // action the gateway swapped. Sending only the text left the browser trusting the hash it was
        // handed by the one party the hash is meant to check.
        var sealedAction = sealer.Action(remoteRunId, approvalId, action.ToolCallId, actionHash, remotelyDecidable,
            new SealedAction(action.Tool, action.ArgumentsJson, request.FullText, action.WorkingDirectory, request.Topic));

        store.Enqueue(remoteRunId, RemoteEventKind.ApprovalRequested,
            sequence => sealer.Detail(remoteRunId, sequence, RemoteEventKind.ApprovalRequested, request.Topic),
            approval: new ApprovalRequest(approvalId, action.ToolCallId, actionHash, remotelyDecidable, sealedAction));

        // Published first, refused second, and the desktop is never asked. Publishing it anyway is
        // the point: the panel shows what the run wanted to do and that it was refused, which is
        // the difference between a rule and a silence.
        if (!remotelyDecidable)
        {
            Report(approvalId, actionHash, ApprovalOutcome.Denied);
            return Refuse(request, "a task started from the web may not run shell commands");
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(timeout);

        var remote = approvals.WaitAsync(approvalId, actionHash);
        Task<DecisionOutcome>? local = null;

        try
        {
            local = desktop.RequestAsync(request, race.Token);
            var winner = await Task.WhenAny(local, remote);
            race.Token.ThrowIfCancellationRequested();

            // Whoever lost is told to stop: the desktop card closes, and a command arriving for
            // this request afterwards finds nothing waiting.
            await race.CancelAsync();
            approvals.Forget(approvalId);

            // Cancellation starts asynchronous card cleanup; finish it before advancing the run.
            if (winner != local)
            {
                try { await local; }
                catch (OperationCanceledException) when (race.IsCancellationRequested) { }
            }
            ct.ThrowIfCancellationRequested();

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
        catch (Exception)
        {
            Report(approvalId, actionHash, ApprovalOutcome.Invalidated);
            throw;
        }
        finally
        {
            await race.CancelAsync();
            approvals.Forget(approvalId);
            if (local is not null)
            {
                // Observe the losing task on every exit, including cancellation/error paths.
                try { await local; }
                catch (Exception) { /* Preserve the outcome or original exception reported above. */ }
            }
        }
    }

    private void Report(string approvalId, string actionHash, ApprovalOutcome outcome)
        => store.Enqueue(remoteRunId, RemoteEventKind.ApprovalResolved,
            sequence => sealer.Detail(remoteRunId, sequence, RemoteEventKind.ApprovalResolved, outcome.ToString()),
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
}
