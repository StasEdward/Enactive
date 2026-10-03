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
/// <param name="administration">
/// Who changes the trusted list when a browser removes or endorses a device. A function, asked when such a
/// command arrives, because the application makes one per connection; null, or a function answering null,
/// on a computer with no key store to change - those commands are then refused with a reason.
/// </param>
public sealed class RemoteRunner(
    HostStore store,
    RemoteApprovals approvals,
    Sealer sealer,
    Func<OpenedStart, Func<IDecisionHandler, IDecisionHandler>, CancellationToken,
        Task<RemotePreparation>> prepare,
    Func<KeyAdministration?>? administration = null)
{
    /// <summary>
    /// How often a command that fails on this computer's side - not one refused - is tried before it is given
    /// up on. Retried for its whole lifetime, a start that could never be carried out - a forged one naming a
    /// run another start had opened, which fails on the run's key - failed every fifteen seconds for a day,
    /// and put the same failure in the status line each time.
    /// </summary>
    public const int MaxAttempts = 5;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentQueue<DeliveryNotice> _notices = new();

    /// <summary>Runs currently executing, by remote run id. For the desktop to show, and for tests.</summary>
    public IReadOnlyCollection<string> Running => _running.Keys.ToArray();

    /// <summary>Commands this computer refused and had no run to report on, for the desktop to show.</summary>
    public IReadOnlyCollection<DeliveryNotice> Notices => _notices.ToArray();

    /// <summary>Raised when a notice is recorded. Not on the UI thread.</summary>
    public event Action<DeliveryNotice>? Noticed;

    /// <summary>
    /// Carries out the commands one Sync brought, in <see cref="CommandOrder"/>: each is opened here, one after
    /// another, and only a start's run is handed to <paramref name="runInBackground"/>.
    ///
    /// <para>Opened here and not on the background task, because opening is where the key is chosen: a start
    /// opened on its own task raced the removal listed with it, and opened under the key the removal was
    /// replacing. The removals and endorsements are carried out to the end before the next command is opened,
    /// so whatever follows meets the trusted list and the key they left.</para>
    /// </summary>
    /// <param name="runInBackground">
    /// Runs a started task without holding up the delivery loop: a run takes minutes, and nothing it produced
    /// would be sent until it finished.
    /// </param>
    /// <param name="failed">
    /// Told of a command that failed on this computer's side - not a refusal, which is said where refusals
    /// are. Without it one failure ended the batch, and the commands after it, already acknowledged, were
    /// never carried out. The failed command itself is not marked carried out, so the next turn tries it
    /// again, up to <see cref="MaxAttempts"/> times; it is told only when a failure says something the last
    /// one did not, and giving up is said once, as a notice.
    /// </param>
    public async Task ApplyAllAsync(
        IReadOnlyList<HostCommand> commands,
        Action<Func<CancellationToken, Task>> runInBackground,
        Action<HostCommand, Exception> failed,
        CancellationToken ct = default)
    {
        foreach (var command in CommandOrder.Arrange(commands))
        {
            try
            {
                if (command.Kind != CommandKind.StartTask)
                {
                    await ApplyAsync(command, ct);
                    continue;
                }

                OpenedStart start;
                try
                {
                    start = sealer.OpenStart(command);
                }
                catch (CommandRefusedException refused)
                {
                    Refuse(command, refused);
                    continue;
                }

                // Claimed here, before the run is handed on, and not on the run's own task. Claimed there, a
                // cancel of it in the same batch - the person pressed Cancel while this computer was off -
                // was carried out first, found no run to stop and was used up, and the task ran when the
                // run's task got going.
                if (Claim(start, command.Id) is { } cancellation)
                {
                    runInBackground(token => RunAsync(start, cancellation, token));
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Failure(command, failure, failed);
            }
        }
    }

    /// <summary>
    /// A command that failed on this computer's side: counted, said when the failure is new, and given up on
    /// - marked carried out, and said once - at the <see cref="MaxAttempts"/>th. A command the inbox does not
    /// hold, handed in directly, is only said.
    ///
    /// <para>A removal or an endorsement is never given up on: it is tried for as long as it may wait, and the
    /// inbox says so when that runs out. Given up after five failures - a minute of a disk that would not
    /// write - a removal was dropped, and the device the person removed stayed trusted here with the key
    /// unchanged. Both are safe to try again: a removal already made, or an endorsement already trusted,
    /// changes nothing.</para>
    /// </summary>
    private void Failure(HostCommand command, Exception failure, Action<HostCommand, Exception> failed)
    {
        var attempt = store.RecordFailure(command.Id, failure.Message);
        var mayGiveUp = command.Kind is not (CommandKind.RevokeDevice or CommandKind.EndorseDevice);

        if (mayGiveUp && attempt is { Attempts: >= MaxAttempts })
        {
            store.MarkApplied(command.Id);
            Notice(new DeliveryNotice("GaveUp",
                $"A remote {command.Kind} command could not be carried out after {MaxAttempts} attempts: {failure.Message}"));
            return;
        }

        if (attempt is not { NewReason: false })
        {
            failed(command, failure);
        }
    }

    /// <summary>
    /// Carries out one accepted command.
    ///
    /// <para>The caller has already written it down; by the time this is reached, the only question
    /// left is what the command means - and whether the owner sent it. Every kind is opened before
    /// anything is done, and a command that does not open does nothing at all.</para>
    ///
    /// <para>Once carried out, or refused, it is marked so in the inbox, which hands back every command
    /// not marked on every turn. Not before: a command that failed on this computer's side, or whose
    /// carrying out the application closing cut short, is tried again on the next turn instead of lost.
    /// A start is marked by the claim that opens its run, before it runs (<see cref="Claim"/>).</para>
    /// </summary>
    public async Task ApplyAsync(HostCommand command, CancellationToken ct = default)
    {
        try
        {
            switch (command.Kind)
            {
                case CommandKind.StartTask:
                    var start = sealer.OpenStart(command);
                    if (Claim(start, command.Id) is { } cancellation)
                    {
                        await RunAsync(start, cancellation, ct);
                    }

                    return;

                case CommandKind.CancelRun:
                    var runId = sealer.OpenCancel(command).RunId;
                    if (!TryCancel(runId))
                    {
                        EndBeforeItBegan(runId);
                    }

                    break;

                case CommandKind.ResolveApproval:
                    Answer(sealer.OpenDecision(command));
                    break;

                // The browser removed the device at the gateway before it sent this, so the gateway is not
                // asked again: this computer distrusts it and gives everyone else a new key. The key store
                // marks the command carried out in the step that replaces the key: marked after it, a crash
                // between the two had the removal carried out again under the key it had itself replaced,
                // and refused as stale - said to the person as a refused command, though it had happened.
                case CommandKind.RevokeDevice:
                    var revocation = sealer.OpenRevocation(command);
                    await Administration().RevokeAsync(
                        revocation.DeviceId, KeyAdministration.RemovedFromBrowser, command.Id, ct);
                    break;

                // Marked after, not in the same step: an endorsement changes no key, so carried out again
                // after a crash it opens as before and finds the device already trusted with that key.
                case CommandKind.EndorseDevice:
                    var endorsement = sealer.OpenEndorsement(command);
                    await Administration().EndorseAsync(endorsement, ct);
                    break;

                default:
                    throw new NotSupportedException($"Command kind {command.Kind} is not one this build carries out.");
            }

            store.MarkApplied(command.Id);
        }
        catch (CommandRefusedException refused)
        {
            // The opening throws this, and a device command the trusted list will not take: RunAsync
            // catches everything a run can throw and reports it as the run's ending, so nothing that began
            // running is ever reported as refused.
            Refuse(command, refused);
        }
    }

    /// <summary>
    /// Who changes the trusted list. A refusal rather than an exception when there is nobody: thrown, it
    /// reached the person as "could not be carried out", as if this computer had failed at something it
    /// can do.
    /// </summary>
    private KeyAdministration Administration()
        => administration?.Invoke() ?? throw new CommandRefusedException(KeyAdministration.CannotManageDevices);

    /// <summary>
    /// A command that did not open, said where a person will see it.
    ///
    /// <para>A start is the one kind with a place of its own: the panel already shows a queued run for
    /// it, and a run that never moved would leave the owner waiting on something that is never going to
    /// happen. So the run is opened and at once ended Failed with the reason, and nothing runs. Opening
    /// it also claims the command, so a redelivery of the same forgery is not even looked at again.
    /// Every other kind has nothing to report on, and is a notice on this computer.</para>
    ///
    /// <para>A refusal is a final answer, so the command is marked carried out: left unmarked, the inbox
    /// would hand it back on every turn and the person would hear the same refusal every fifteen
    /// seconds for as long as the command lived.</para>
    /// </summary>
    private void Refuse(HostCommand command, CommandRefusedException refused)
    {
        var reason = refused.Message;

        if (command.Kind == CommandKind.StartTask && RunOf(command) is { } runId)
        {
            if (store.BeginRun(command.Id, runId))
            {
                // A start sealed under the key a removal replaced is most likely the owner's own, sent from a
                // device that had not yet received the new key: it is asked for again, not called forged.
                var detail = refused.SendAgain
                    ? $"This computer refused the request: {reason}."
                    : $"This computer refused the request: {reason}. It did not come from a device this computer trusts.";

                store.Enqueue(runId, RemoteEventKind.Failed, sequence => sealer.Detail(
                    runId, sequence, RemoteEventKind.Failed, detail));
            }

            return;
        }

        store.MarkApplied(command.Id);
        Notice(new DeliveryNotice("Refused", $"{command.Kind} {command.Id}: {reason}"));
    }

    private void Notice(DeliveryNotice notice)
    {
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
    public void Cancel(string runId) => TryCancel(runId);

    /// <summary>Whether there was a run by this id on this computer to ask.</summary>
    private bool TryCancel(string runId)
    {
        if (!_running.TryGetValue(runId, out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between being found and being asked; there is nothing left to stop.
        }

        return true;
    }

    /// <summary>
    /// A cancel for a run not going on this computer, whose start is still in the inbox, not carried out:
    /// the start is claimed and its run ended Cancelled there and then, so it never runs. Left alone, the
    /// cancel was used up on nothing, and the start ran when its turn came - after the person had cancelled
    /// it. Ending it Cancelled at once is no guess about what happened, as it would be for a run going on:
    /// nothing of it has run.
    ///
    /// <para>The start is found by the run its plaintext names, which the gateway writes: one that pointed
    /// a cancel at another start could only stop a task, which a gateway can do by dropping it anyway.</para>
    /// </summary>
    private void EndBeforeItBegan(string runId)
    {
        foreach (var start in store.Unapplied(DateTimeOffset.UtcNow))
        {
            if (start.Kind == CommandKind.StartTask && RunOf(start) == runId && store.BeginRun(start.Id, runId))
            {
                Report(runId, RemoteEventKind.Cancelled, CancelledBeforeItBegan);
                return;
            }
        }
    }

    private const string CancelledBeforeItBegan = "Cancelled at the owner's request before it began.";

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

    /// <summary>
    /// Claims a start and opens its run, and makes it cancellable, BEFORE anything executes - and before
    /// its run is handed to a task of its own, so a cancel carried out next finds it. Null means a
    /// redelivery of a command this machine already carried out, and the correct response to that is to
    /// do nothing at all.
    /// </summary>
    private CancellationTokenSource? Claim(OpenedStart task, string commandId)
    {
        if (!store.BeginRun(commandId, task.RunId))
        {
            return null;
        }

        var cancellation = new CancellationTokenSource();
        _running[task.RunId] = cancellation;
        return cancellation;
    }

    /// <summary>Runs a claimed start, unless it was cancelled before it began.</summary>
    private async Task RunAsync(OpenedStart task, CancellationTokenSource cancellation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, ct);

        try
        {
            if (cancellation.IsCancellationRequested)
            {
                EndOnce(task.RunId, RemoteEventKind.Cancelled, CancelledBeforeItBegan);
                return;
            }

            var prepared = await prepare(
                task,
                desktop => new RemoteDecisionHandler(
                    desktop, store, approvals, sealer, task.RunId, RemoteDecisionHandler.DefaultTimeout),
                linked.Token);

            // Whatever the preparation opened is closed here, however this ends - cancelled,
            // failed, or finished. `await using` on a null is a no-op, so a preparation with
            // nothing to close says so by leaving it null rather than by handing over a stub.
            await using (prepared.Resources)
            {
                Report(task.RunId, RemoteEventKind.Running, $"Started: {task.Title}");
                store.MarkRunState(task.RunId, LocalRunState.Running);

                await ConsumeAsync(task.RunId, prepared.Engine, prepared.Intent, linked.Token);
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
            cancellation.Dispose();
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
