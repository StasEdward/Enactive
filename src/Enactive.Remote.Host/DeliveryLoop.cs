namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts;

/// <summary>What the loop noticed, for the application to show a person.</summary>
public sealed record DeliveryNotice(string Kind, string Detail);

/// <summary>
/// Delivery, and nothing else.
///
/// <para>This class accepts commands, remembers them, reports about them and gives up on them in
/// the right order. It does not carry any of them out - at stage 3 it cannot, since this assembly
/// has no reference to the engine. What a StartTask actually does arrives in stage 4 as a callback;
/// everything here is what has to be true whatever that callback turns out to be.</para>
///
/// <para>It takes the <see cref="Sealer"/> for the one event it writes itself, the Interrupted report
/// on startup: every event detail travels sealed, and one written in the clear would be stored by the
/// gateway and fail to open in the browser.</para>
///
/// <para>It delivers the key store's grants too, when it is given the outbox they wait in: a device is
/// trusted on this computer before the gateway has its grant, and only this loop talks to the gateway.
/// </para>
/// </summary>
public sealed class DeliveryLoop(HostStore store, IGatewayConnection gateway, Sealer sealer, IGrantOutbox? grants = null)
{
    /// <summary>
    /// How often an event may be refused for a reason we do not understand before it is parked.
    ///
    /// <para>It exists because of the unknown-code rule. Retrying is what never silently loses an
    /// event, and per-run ordering means one stuck event holds up every later event for that run -
    /// so without a limit, "never lose anything" turns into "never deliver anything again".</para>
    /// </summary>
    public const int MaxAttempts = 10;

    /// <summary>
    /// The most grants one PublishGrants call carries. The gateway refuses a larger call whole, so a
    /// computer owing more - a rotation to many devices - would otherwise never deliver any of them.
    /// </summary>
    public const int MaxGrantsPerCall = 50;

    private readonly List<DeliveryNotice> _notices = [];

    /// <summary>Set when the gateway said something no reconnection will fix.</summary>
    public bool Stopped { get; private set; }

    public IReadOnlyList<DeliveryNotice> Notices => _notices;

    /// <summary>Raised for every notice as it is added, so a person can be told now rather than when someone reads the list.</summary>
    public event Action<DeliveryNotice>? Noticed;

    /// <summary>
    /// Called once at startup, before anything else.
    ///
    /// <para>Every run this machine opened and never finished is reported Interrupted. It is the
    /// only honest thing to say: the record proves the run may have done something, and nothing
    /// recorded how it ended. This is also what the owner sees after the desktop was closed while a
    /// remote task was running - which, with the Host living inside the application, is the normal
    /// way for it to happen rather than a rare failure.</para>
    /// </summary>
    public void RecoverInterruptedRuns()
    {
        foreach (var runId in store.RunsLeftInFlight())
        {
            store.Enqueue(runId, RemoteEventKind.Interrupted, sequence => sealer.Detail(
                runId, sequence, RemoteEventKind.Interrupted,
                "The application stopped while this run was in progress, so how it ended is unknown."));

            Notice(new DeliveryNotice("Interrupted", runId));
        }
    }

    /// <summary>
    /// One turn: publish what is owed - grants, then events - then ask for work, and hand back every
    /// command received and not yet carried out, in <see cref="CommandOrder"/>.
    ///
    /// <para>Publishing comes first so that a command accepted in this same turn is never reported
    /// about before earlier events have gone - and so that a Host with a backlog spends its
    /// connection on clearing it rather than on taking more on. Grants go before events because a
    /// device reads an event only with the key a grant gives it: an event that arrived first would
    /// sit on the phone as something it cannot open.</para>
    ///
    /// <para><b>What is handed back is read from the inbox, not from what this Sync brought.</b> Only
    /// a command new to this Sync used to be handed back, so one that was written down and then not
    /// carried out - its acknowledgement failed, a later one in its batch failed, the application
    /// closed - was met again as "not new" and dropped, or never met again at all once acknowledged.
    /// The inbox keeps it until the runner marks it carried out (<see cref="HostStore.MarkApplied"/>),
    /// so the first turn after a start brings back what the last process left, and every turn brings
    /// back what an earlier one could not finish. A start handed back twice still runs once: the
    /// runner claims it before it runs (<see cref="HostStore.BeginRun"/>).</para>
    /// </summary>
    public async Task<IReadOnlyList<HostCommand>> TurnAsync(
        IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct = default)
    {
        if (Stopped)
        {
            return [];
        }

        await FlushGrantsAsync(ct);

        if (Stopped)
        {
            return [];
        }

        await FlushAsync(ct);

        if (Stopped)
        {
            return [];
        }

        IReadOnlyList<HostCommand> commands;
        try
        {
            commands = await gateway.SyncAsync(workspaces, ct);
        }
        catch (GatewayRefusedException refused) when (refused.Code == FaultCode.QuotaExceeded)
        {
            // A wait, not a lost connection. Escaping as an exception, it sent the service round
            // its reconnect loop - more calls against the very limit that refused this one. What
            // the inbox holds needs nothing from the gateway to be carried out, so it is not kept
            // waiting with it.
            return Owed([]);
        }
        catch (GatewayRefusedException refused) when (refused.Disposition == FaultDisposition.Fatal)
        {
            // The credential is gone or the protocols differ. Reconnecting hears the same answer.
            Stopped = true;
            Notice(new DeliveryNotice("Stopped", $"{refused.Code} - {refused.Message}"));
            return [];
        }

        var acknowledging = true;

        foreach (var command in commands)
        {
            if (command.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                continue;
            }

            // Written down first. Acknowledging before this and then crashing loses the command for
            // good: the gateway stops re-delivering what has been accepted, and this machine would
            // have no record that it ever existed. A redelivery is written down as nothing new.
            store.Accept(command);

            if (!acknowledging)
            {
                continue;
            }

            try
            {
                await gateway.AcknowledgeAsync(command.Id, ct);
                store.MarkAcknowledged(command.Id);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // Not a reason to leave the batch behind. Thrown out of the turn, it took every command
                // already written down with it - acknowledged ones the gateway would not hand over again
                // among them. The rest of the batch is still written down but not acknowledged: on a
                // connection that has just failed each call would only wait out its own failure, and the
                // gateway hands them over again, to be acknowledged then. The next Sync finds out whether
                // the connection is gone, or the credential - and stops the loop if so.
                acknowledging = false;
            }
        }

        return Owed(commands);
    }

    /// <summary>
    /// Every command written down and not yet carried out, in <see cref="CommandOrder"/>.
    ///
    /// <para>As the inbox has it, never as the gateway just sent it: a redelivery under the same id is
    /// written down as nothing new, and acting on its text instead would let the gateway change a
    /// command after this computer received it. The gateway's own record is handed on only when it is
    /// that same command, so its routing fields are kept where they can be.</para>
    /// </summary>
    private IReadOnlyList<HostCommand> Owed(IReadOnlyList<HostCommand> synced)
    {
        var owed = store.Unapplied(DateTimeOffset.UtcNow).Select(stored =>
            synced.FirstOrDefault(c => c.Id == stored.Id && c.Kind == stored.Kind && c.Payload == stored.Payload)
                ?? stored);

        return CommandOrder.Arrange(owed);
    }

    /// <summary>
    /// Sends what is owed, one event per run at a time, and acts on the answer.
    ///
    /// <para>The disposition decides everything: <see cref="FaultDisposition.Drop"/> means the far
    /// end has settled this and the item goes; <see cref="FaultDisposition.Fatal"/> means stop and
    /// keep the queue intact; anything else - including a code from a newer gateway that this build
    /// has never heard of - is a retry, counted.</para>
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Until nothing moves. NextOwed returns only the EARLIEST event of each run, so draining a
        // backlog takes as many passes as there are events - and without this loop a run with a
        // queue behind it would give up one event per turn, which at a fifteen-second Sync is not
        // delivery, it is a trickle. It ends when a whole pass changed nothing, which is the case
        // where every remaining item is waiting to be retried.
        while (!Stopped && !ct.IsCancellationRequested)
        {
            var owedNow = store.NextOwed();

            if (owedNow.Count == 0)
            {
                return;
            }

            var moved = false;

            foreach (var owed in owedNow)
            {
                if (Stopped || ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await gateway.PublishAsync(owed.Event, ct);
                    store.Discard(owed.EventId);
                    moved = true;
                }
                catch (GatewayRefusedException refused) when (refused.Code == FaultCode.QuotaExceeded)
                {
                    // The account is over its rate: the gateway is saying "later", not "never". Counted
                    // toward parking, ten refusals in one busy minute parked a good event and sent the
                    // rest of its run out with a hole in front of it. Nothing more goes this pass,
                    // because every further call would be refused the same way.
                    return;
                }
                catch (GatewayRefusedException refused)
                {
                    moved |= Handle(owed, refused.Disposition, refused.Code, refused.Message);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // Not a coded refusal, so not a verdict about the event: the socket closed, or
                    // the server was restarting. Keep it.
                    moved |= Handle(owed, FaultDisposition.Retry, null, failure.Message);
                }
            }

            if (!moved)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends the grants this computer owes, <see cref="MaxGrantsPerCall"/> at a time.
    ///
    /// <para>A grant refused as malformed, or for a device or computer the gateway no longer has, is
    /// discarded and said: sending it again cannot make it acceptable, and kept it would be refused on
    /// every turn for as long as the application ran. Because the gateway refuses a call whole, a
    /// refused call of several grants is sent again one grant at a time, so the good ones in it are
    /// not discarded with the bad one. A grant refused for the account's rate, or that did not reach
    /// the gateway at all, is kept for the next turn.</para>
    /// </summary>
    public async Task FlushGrantsAsync(CancellationToken ct = default)
    {
        if (grants is null)
        {
            return;
        }

        foreach (var call in grants.PendingGrants().Chunk(MaxGrantsPerCall))
        {
            if (Stopped || ct.IsCancellationRequested || !await PublishGrantsAsync(grants, call, ct))
            {
                return;
            }
        }
    }

    /// <summary>Returns whether to go on with the next call; false when the gateway said to wait or stop.</summary>
    private async Task<bool> PublishGrantsAsync(IGrantOutbox outbox, PendingGrant[] call, CancellationToken ct)
    {
        try
        {
            await gateway.PublishGrantsAsync([.. call.Select(p => p.Grant)], ct);
        }
        catch (GatewayRefusedException refused) when (refused.Disposition == FaultDisposition.Fatal)
        {
            Stopped = true;
            Notice(new DeliveryNotice("Stopped", $"{refused.Code} - {refused.Message}"));
            return false;
        }
        catch (GatewayRefusedException refused) when (SettlesAGrant(refused.Code))
        {
            if (call.Length > 1)
            {
                foreach (var single in call)
                {
                    if (Stopped || ct.IsCancellationRequested || !await PublishGrantsAsync(outbox, [single], ct))
                    {
                        return false;
                    }
                }

                return true;
            }

            outbox.DiscardGrant(call[0].Id);
            Notice(new DeliveryNotice("GrantDropped",
                $"The key grant for device {call[0].Grant.DeviceId} (epoch {call[0].Grant.Epoch}) was refused "
                + $"and will not be sent again: {refused.Code} - {refused.Message}"));
            return true;
        }
        catch (GatewayRefusedException)
        {
            // Quota, or a code this build does not know: kept, for the next turn.
            return false;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Not a verdict about the grants - the socket closed. The Sync after this finds out.
            return false;
        }

        foreach (var delivered in call)
        {
            outbox.DiscardGrant(delivered.Id);
        }

        return true;
    }

    /// <summary>
    /// The gateway's answer for a device or computer it does not have. It has no code in the table,
    /// because nothing on an event's path can meet it - so a grant refused with it would be classed
    /// as an unknown code and sent again on every turn, for ever.
    /// </summary>
    private const string NotFound = "not-found";

    /// <summary>The refusals that settle a grant: the Drop codes, and <see cref="NotFound"/>.</summary>
    private static bool SettlesAGrant(string code)
        => code == NotFound || RemoteFaults.DispositionOf(code) == FaultDisposition.Drop;

    private void Notice(DeliveryNotice notice)
    {
        _notices.Add(notice);
        Noticed?.Invoke(notice);
    }

    /// <summary>Returns whether the queue changed, which is how <see cref="FlushAsync"/> knows to look again.</summary>
    private bool Handle(OutboxItem owed, FaultDisposition disposition, string? code, string message)
    {
        switch (disposition)
        {
            case FaultDisposition.Drop:
                store.Discard(owed.EventId);

                // Said out loud rather than swallowed. Dropping is correct and it is still an event
                // that will never reach the timeline, so somebody gets to know which one.
                Notice(new DeliveryNotice("Dropped", $"{owed.RunId} #{owed.Sequence}: {code} - {message}"));
                return true;

            case FaultDisposition.Fatal:
                Stopped = true;
                Notice(new DeliveryNotice("Stopped", $"{code} - {message}"));
                return false;

            default:
                store.RecordAttempt(owed.EventId, code);

                if (owed.Attempts + 1 < MaxAttempts)
                {
                    return false;
                }

                store.Park(owed.EventId);
                Notice(new DeliveryNotice("Parked",
                    $"{owed.RunId} #{owed.Sequence} was refused {MaxAttempts} times and is kept "
                    + $"but no longer sent, so this run's later events can go. Last: {code ?? "no code"} - {message}"));

                // Parking IS progress: the run's next event has just become sendable.
                return true;
        }
    }
}
