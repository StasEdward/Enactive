namespace Enactive.Remote.Host;

using System.Collections.Concurrent;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;

/// <summary>
/// What the application hands back for one task: the engine to run it with, the intent to run, and
/// anything opened along the way that has to be closed again.
///
/// <para><see cref="Resources"/> is optional because not every composition opens anything. When it
/// is present it is disposed once the run has finished, however it finished - completed, failed or
/// cancelled.</para>
/// </summary>
public sealed record RemotePreparation(
    IOrchestrator Engine,
    Intent Intent,
    IAsyncDisposable? Resources = null);

/// <summary>
/// Turns a remote command into a real run.
///
/// <para>This is the whole of the engine binding, and it is deliberately small: the engine gets no
/// new concept. A StartTask becomes an <see cref="Intent"/>, the events that intent produces become
/// queued remote events, and a CancelRun cancels the token the run was started with. Everything
/// that makes remote access survive a lost connection or a crash lives in
/// <see cref="HostStore"/> and <see cref="DeliveryLoop"/>, not here.</para>
/// </summary>
/// <param name="prepare">
/// Builds the engine and the Intent for one task. Supplied by the application, because assembling a
/// <c>WorkContext</c> and choosing providers means knowing which workspace an id refers to and what
/// is on this machine - facts this library deliberately does not have. It is also the one place
/// that can refuse a workspace id it does not recognise.
///
/// <para>It is handed a <c>wrap</c> function and must install what that returns as the run's
/// decision handler. That is how a permission becomes answerable from the phone without the
/// desktop losing it: the wrapper races the two. Ignoring <c>wrap</c> produces a run that works and
/// can only be answered at the machine - a defensible choice, and a deliberate one.</para>
///
/// <para>Anything it opens that must be closed goes in <see cref="RemotePreparation.Resources"/>,
/// which is disposed when the run ends however it ends. The first version returned only the engine
/// and the intent, and the first real caller had to open MCP tool servers to build one - which are
/// child processes. They would have been left running, one set per remote run, until the desktop
/// was closed.</para>
///
/// <para>It is handed the task as it OPENED, never the payload: the payload's plaintext is written by
/// the gateway, and only what <see cref="Sealer"/> opened and checked came from the owner.</para>
/// </param>
public sealed class RemoteRunner(
    HostStore store,
    RemoteApprovals approvals,
    Sealer sealer,
    Func<OpenedStart, Func<IDecisionHandler, IDecisionHandler>, CancellationToken,
        Task<RemotePreparation>> prepare)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentQueue<DeliveryNotice> _notices = new();

    /// <summary>Runs currently executing, by remote run id. For the desktop to show, and for tests.</summary>
    public IReadOnlyCollection<string> Running => _running.Keys.ToArray();

    /// <summary>Commands this computer refused and had no run to report on, for the desktop to show.</summary>
    public IReadOnlyCollection<DeliveryNotice> Notices => _notices.ToArray();

    /// <summary>Raised when a notice is recorded. Not on the UI thread.</summary>
    public event Action<DeliveryNotice>? Noticed;

    /// <summary>
    /// Carries out one accepted command.
    ///
    /// <para>The caller has already written it down and acknowledged it; by the time this is
    /// reached, the only question left is what the command means - and whether the owner sent it.
    /// Every kind is opened before anything is done, and a command that does not open does nothing
    /// at all.</para>
    /// </summary>
    public async Task ApplyAsync(HostCommand command, CancellationToken ct = default)
    {
        try
        {
            switch (command.Kind)
            {
                case CommandKind.StartTask:
                    var start = sealer.OpenStart(command);
                    await StartAsync(start, command.Id, ct);
                    return;

                case CommandKind.CancelRun:
                    Cancel(sealer.OpenCancel(command).RunId);
                    return;

                case CommandKind.ResolveApproval:
                    Answer(sealer.OpenDecision(command));
                    return;

                default:
                    throw new NotSupportedException($"Command kind {command.Kind} is not one this build carries out.");
            }
        }
        catch (CommandRefusedException refused)
        {
            // Only the opening throws this: StartAsync catches everything a run can throw and reports
            // it as the run's ending, so nothing that began running is ever reported as refused.
            Refuse(command, refused.Message);
        }
    }

    /// <summary>
    /// A command that did not open, said where a person will see it.
    ///
    /// <para>A start is the one kind with a place of its own: the panel already shows a queued run for
    /// it, and a run that never moved would leave the owner waiting on something that is never going to
    /// happen. So the run is opened and at once ended Failed with the reason, and nothing runs. Opening
    /// it also claims the command, so a redelivery of the same forgery is not even looked at again.
    /// Every other kind has nothing to report on, and is a notice on this computer.</para>
    /// </summary>
    private void Refuse(HostCommand command, string reason)
    {
        if (command.Kind == CommandKind.StartTask && RunOf(command) is { } runId)
        {
            if (store.BeginRun(command.Id, runId))
            {
                store.Enqueue(runId, RemoteEventKind.Failed, sequence => sealer.Detail(
                    runId, sequence, RemoteEventKind.Failed,
                    $"This computer refused the request: {reason}. It did not come from a device this computer trusts."));
            }

            return;
        }

        var notice = new DeliveryNotice("Refused", $"{command.Kind} {command.Id}: {reason}");
        _notices.Enqueue(notice);
        Noticed?.Invoke(notice);
    }

    /// <summary>The run a start names in its plaintext, if the payload can be read at all.</summary>
    private static string? RunOf(HostCommand command)
    {
        try
        {
            return RemoteJson.Deserialize<StartTaskPayload>(command.Payload).RunId is { Length: > 0 } runId ? runId : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks a run to stop. Asking is all this does: the run reports Cancelled only once it has
    /// actually stopped, because external effects may already have happened and a timeline that
    /// said "cancelled" at the moment of the request would be making that up.
    /// </summary>
    public void Cancel(string runId)
    {
        if (_running.TryGetValue(runId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    /// <summary>
    /// Hands a queued remote answer to whoever is waiting for it, if anyone still is.
    ///
    /// <para>Being handed this command is not authorisation. The desktop may have answered first,
    /// the request may have expired, the run may be gone - none of which the gateway can know,
    /// because all three are facts about this machine. <see cref="RemoteApprovals"/> checks them and
    /// simply does nothing when the answer no longer applies: the outcome that actually happened is
    /// already on its way as an ApprovalResolved event, so there is nothing to report and nothing
    /// to correct.</para>
    /// </summary>
    private void Answer(DecisionAuthorization answer)
        => approvals.TryAnswer(answer.ApprovalId, answer.ActionHash, answer.Decision);

    private async Task StartAsync(OpenedStart task, string commandId, CancellationToken ct)
    {
        // The claim and the run record, in one transaction, BEFORE anything executes. False means a
        // redelivery of a command this machine already carried out, and the correct response to that
        // is to do nothing at all.
        if (!store.BeginRun(commandId, task.RunId))
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[task.RunId] = cancellation;

        try
        {
            var prepared = await prepare(
                task,
                desktop => new RemoteDecisionHandler(
                    desktop, store, approvals, sealer, task.RunId, RemoteDecisionHandler.DefaultTimeout),
                cancellation.Token);

            // Whatever the preparation opened is closed here, however this ends - cancelled,
            // failed, or finished. `await using` on a null is a no-op, so a preparation with
            // nothing to close says so by leaving it null rather than by handing over a stub.
            await using (prepared.Resources)
            {
                Report(task.RunId, RemoteEventKind.Running, $"Started: {task.Title}");
                store.MarkRunState(task.RunId, LocalRunState.Running);

                await ConsumeAsync(task.RunId, prepared.Engine, prepared.Intent, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The engine stopped when it was asked to. This is the only place a Cancelled ending is
            // written, and it happens after the fact rather than when the request arrived.
            EndOnce(task.RunId, RemoteEventKind.Cancelled, "Stopped at the owner's request.");
        }
        catch (Exception failure)
        {
            // Whatever went wrong, the run does not get to sit open. A remote owner watching a
            // status that never changes has no way to tell a long step from a dead process.
            EndOnce(task.RunId, RemoteEventKind.Failed, $"The run stopped with an error: {failure.Message}");
        }
        finally
        {
            _running.TryRemove(task.RunId, out _);
        }
    }

    /// <summary>
    /// Reads the run's event stream and queues what the owner should see.
    ///
    /// <para>Nothing is sent from here. Every event goes into the outbox and
    /// <see cref="DeliveryLoop"/> delivers it - so a run is never slowed by the network, and a
    /// connection that drops mid-run costs nothing but time.</para>
    /// </summary>
    private async Task ConsumeAsync(string runId, IOrchestrator engine, Intent intent, CancellationToken ct)
    {
        var ended = false;

        await foreach (var published in engine.SubmitIntentAsync(intent, ct))
        {
            if (EventMapping.Ending(published) is var (kind, detail))
            {
                Report(runId, kind, detail);
                ended = true;
                continue;
            }

            if (EventMapping.Progress(published) is { } line)
            {
                Report(runId, RemoteEventKind.Progress, line);
            }
        }

        // The stream finished without saying how the run ended. That is not something the engine is
        // supposed to do, and the answer is still not to guess: an unfinished run reported as
        // unfinished is recoverable, and one reported as complete is not.
        if (!ended)
        {
            EndOnce(runId, RemoteEventKind.Incomplete,
                "The run stopped without reporting how it ended.");
        }
    }

    /// <summary>
    /// Queues an ending, unless one is already queued.
    ///
    /// <para>Both the catch blocks above and the end of the stream can reach this, and a run has
    /// exactly one ending: a second would be refused by the gateway as an event about a run that
    /// has ended, and the Host would then be arguing with the far end about something it could have
    /// known itself.</para>
    /// </summary>
    private void EndOnce(string runId, RemoteEventKind kind, string detail)
    {
        if (!store.HasEnded(runId))
        {
            Report(runId, kind, detail);
        }
    }

    /// <summary>Queues an event with its sentence sealed for the sequence the store gives it.</summary>
    private void Report(string runId, RemoteEventKind kind, string detail)
        => store.Enqueue(runId, kind, sequence => sealer.Detail(runId, sequence, kind, detail));
}
