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
/// </summary>
public sealed class DeliveryLoop(HostStore store, IGatewayConnection gateway, Sealer sealer)
{
    /// <summary>
    /// How often an event may be refused for a reason we do not understand before it is parked.
    ///
    /// <para>It exists because of the unknown-code rule. Retrying is what never silently loses an
    /// event, and per-run ordering means one stuck event holds up every later event for that run -
    /// so without a limit, "never lose anything" turns into "never deliver anything again".</para>
    /// </summary>
    public const int MaxAttempts = 10;

    private readonly List<DeliveryNotice> _notices = [];

    /// <summary>Set when the gateway said something no reconnection will fix.</summary>
    public bool Stopped { get; private set; }

    public IReadOnlyList<DeliveryNotice> Notices => _notices;

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

            _notices.Add(new DeliveryNotice("Interrupted", runId));
        }
    }

    /// <summary>
    /// One turn: publish what is owed, then ask for work.
    ///
    /// <para>Publishing comes first so that a command accepted in this same turn is never reported
    /// about before earlier events have gone - and so that a Host with a backlog spends its
    /// connection on clearing it rather than on taking more on.</para>
    /// </summary>
    public async Task<IReadOnlyList<HostCommand>> TurnAsync(
        IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct = default)
    {
        if (Stopped)
        {
            return [];
        }

        await FlushAsync(ct);

        if (Stopped)
        {
            return [];
        }

        var commands = await gateway.SyncAsync(workspaces, ct);
        var accepted = new List<HostCommand>();

        foreach (var command in commands)
        {
            if (command.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                continue;
            }

            // Written down first. Acknowledging before this and then crashing loses the command for
            // good: the gateway stops re-delivering what has been accepted, and this machine would
            // have no record that it ever existed.
            var isNew = store.Accept(command);

            await gateway.AcknowledgeAsync(command.Id, ct);
            store.MarkAcknowledged(command.Id);

            // A redelivery of something already carried out is not work. It is the gateway doing
            // exactly what at-least-once delivery means.
            if (isNew && !store.WasApplied(command.Id))
            {
                accepted.Add(command);
            }
        }

        return accepted;
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

    /// <summary>Returns whether the queue changed, which is how <see cref="FlushAsync"/> knows to look again.</summary>
    private bool Handle(OutboxItem owed, FaultDisposition disposition, string? code, string message)
    {
        switch (disposition)
        {
            case FaultDisposition.Drop:
                store.Discard(owed.EventId);

                // Said out loud rather than swallowed. Dropping is correct and it is still an event
                // that will never reach the timeline, so somebody gets to know which one.
                _notices.Add(new DeliveryNotice("Dropped", $"{owed.RunId} #{owed.Sequence}: {code} - {message}"));
                return true;

            case FaultDisposition.Fatal:
                Stopped = true;
                _notices.Add(new DeliveryNotice("Stopped", $"{code} - {message}"));
                return false;

            default:
                store.RecordAttempt(owed.EventId, code);

                if (owed.Attempts + 1 < MaxAttempts)
                {
                    return false;
                }

                store.Park(owed.EventId);
                _notices.Add(new DeliveryNotice("Parked",
                    $"{owed.RunId} #{owed.Sequence} was refused {MaxAttempts} times and is kept "
                    + $"but no longer sent, so this run's later events can go. Last: {code ?? "no code"} - {message}"));

                // Parking IS progress: the run's next event has just become sendable.
                return true;
        }
    }
}
