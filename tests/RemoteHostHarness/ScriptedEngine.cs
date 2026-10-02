namespace RemoteHostHarness;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;

/// <summary>
/// What a run does on the harness: one step, then the ending - and on the way, what the prompt asks for.
///
/// <para>The word <c>ASK</c> in the prompt (a whole word: "TASK" is not it) makes the run ask a permission
/// the panel may answer, through the real <see cref="RemoteDecisionHandler"/>, and say on stdout what it was
/// told (<c>DECIDED</c>).</para>
///
/// <para>The word <c>BADHASH</c> publishes a permission request whose sealed arguments do not hash to the
/// action hash it carries - what a computer hashing differently from the panel, or a faulty one, would send -
/// and then waits until the run is stopped. It is written to the computer's outbox directly: the desktop has
/// no path that publishes such a request, and none is added to it for this.</para>
/// </summary>
internal sealed partial class ScriptedEngine(
    string runId, IDecisionHandler decisions, HostStore store, Sealer sealer) : IOrchestrator
{
    private const string Tool = "echo";
    private const string Shown = """{"cmd":"echo hi"}""";
    private const string Topic = "Run echo on the computer?";
    private const string WorkingDirectory = "/harness/workspace";

    public async IAsyncEnumerable<WorkEvent> SubmitIntentAsync(
        Intent intent, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return Event(EventKind.StepStarted, "[1/1] Working");

        if (BadHash().IsMatch(intent.RawText))
        {
            PublishMismatchedRequest();

            // Until the panel stops the run: a request that is still open is what the test looks at.
            await Task.Delay(Timeout.Infinite, ct);
        }

        if (Ask().IsMatch(intent.RawText))
        {
            var outcome = await decisions.RequestAsync(new DecisionRequest(
                Guid.NewGuid(), Topic, "echo hi",
                [new DecisionOption("allow", "Allow"), new DecisionOption("deny", "Deny")],
                RecommendedOptionId: "allow",
                Subject: Tool,
                FullDetail: "echo hi",
                Action: new BoundAction(Guid.NewGuid(), "call-" + Guid.NewGuid().ToString("N"), Tool, Shown, WorkingDirectory)), ct);

            Computer.Say("DECIDED", $"{runId} {outcome.OptionId}");
            yield return Event(EventKind.StepCompleted, outcome.OptionId == "allow" ? "Allowed: echo hi" : "Denied: echo hi");
        }

        yield return Event(EventKind.TaskCompleted, "All done",
            WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed));
    }

    public IAsyncEnumerable<WorkEvent> ResumeRunAsync(RunCheckpoint checkpoint, WorkContext context, CancellationToken ct)
        => throw new NotSupportedException("The harness does not resume runs.");

    /// <summary>
    /// A request sealed exactly as <see cref="RemoteDecisionHandler"/> seals one - under this run, approval and
    /// tool call, with the hash in the associated data - except that the hash is of other arguments than the
    /// ones sealed. The envelope opens; only the panel's own check of the hash can tell.
    /// </summary>
    private void PublishMismatchedRequest()
    {
        var approvalId = Guid.NewGuid().ToString("N");
        var toolCallId = "call-" + Guid.NewGuid().ToString("N");
        var hash = ActionIdentity.Hash(runId, toolCallId, Tool, WorkingDirectory, """{"cmd":"echo something else"}""");
        var sealedAction = sealer.Action(runId, approvalId, toolCallId, hash, remoteDecidable: true,
            new SealedAction(Tool, Shown, "echo hi", WorkingDirectory, Topic));

        store.Enqueue(runId, RemoteEventKind.ApprovalRequested,
            sequence => sealer.Detail(runId, sequence, RemoteEventKind.ApprovalRequested, Topic),
            approval: new ApprovalRequest(approvalId, toolCallId, hash, RemoteDecidable: true, sealedAction));

        Computer.Say("BADHASH", approvalId);
    }

    private static WorkEvent Event(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, kind, summary, payload);

    [GeneratedRegex(@"\bASK\b")]
    private static partial Regex Ask();

    [GeneratedRegex(@"\bBADHASH\b")]
    private static partial Regex BadHash();
}
