namespace Enactive.Engine.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// Delivery, with nothing being executed. Stage 3 of the remote-access design.
///
/// <para>The point of doing this before the engine is attached is that it can be proven on its own:
/// <c>Enactive.Remote.Host</c> has no reference to Core at this stage, so nothing in these tests
/// CAN run a task. What is being tested is exactly-once acceptance, ordering, what a refusal does
/// to a queued event, and what a crash leaves behind.</para>
///
/// <para>A crash is a real one, as far as this store is concerned: the object is disposed and the
/// file reopened. Nothing is carried over in memory.</para>
/// </summary>
public sealed class RemoteDeliveryTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "enactive-remote-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_folder, "remote.db");

    private HostStore Open() => new(DatabasePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that outlives one test run is litter, not a failure.
        }
    }

    private static readonly FixedHostKeys Keys = new();

    private static HostCommand Start(string commandId, string runId) => Keys.Start(commandId, runId);

    private static DeliveryLoop Loop(HostStore store, IGatewayConnection gateway) => new(store, gateway, Keys.Sealer());

    // ── accepting a command exactly once ────────────────────────────────────

    // A redelivered command recognised across a restart is RemoteSealingTests'
    // Review_focus_3_a_replayed_command_id_is_recognised_after_a_restart: it is the replay half of
    // refusing a forged command, and lives with the other half.

    /// <summary>
    /// A start is claimed once. Two attempts to open the same run - a redelivery, or two threads -
    /// and only one of them is told to go ahead.
    /// </summary>
    [Fact]
    public void A_start_command_opens_its_run_only_once()
    {
        using var store = Open();
        store.Accept(Start("command-1", "run-1"));

        Assert.True(store.BeginRun("command-1", "run-1"));
        Assert.False(store.BeginRun("command-1", "run-1"));
    }

    /// <summary>
    /// The ordering the whole design rests on.
    ///
    /// <para>The record that the command was applied is written BEFORE the run begins, so a crash
    /// in between leaves "this may have run" rather than nothing. Recovery then reports Interrupted
    /// instead of starting the task a second time: we do not know what happened, so we say we do
    /// not know. The alternative is doing the work twice, which for a task that writes files is not
    /// a recoverable mistake.</para>
    /// </summary>
    [Fact]
    public void A_crash_between_claiming_a_start_and_reporting_it_is_reported_as_interrupted()
    {
        using (var store = Open())
        {
            store.Accept(Start("command-1", "run-1"));
            store.BeginRun("command-1", "run-1");

            // and here the process dies, before a single event was queued
        }

        using (var store = Open())
        {
            Assert.True(store.WasApplied("command-1"));

            var loop = Loop(store, new FakeGateway());
            loop.RecoverInterruptedRuns();

            var owed = Assert.Single(store.NextOwed());
            Assert.Equal(RemoteEventKind.Interrupted, owed.Event.Kind);
            Assert.Equal("run-1", owed.RunId);

            // Sealed like every other detail: written in the clear, it would be the one sentence the
            // browser could not open.
            Assert.Contains("stopped while this run was in progress", Keys.OpenDetail(owed.Event), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A run that reported how it ended is finished, and recovery says nothing about it.
    ///
    /// <para>Asserted on what REACHES the gateway, not on what is owed next. The first version of
    /// this test looked at <c>NextOwed</c>, which returns only the earliest event of each run - so
    /// an Interrupted queued behind two earlier events was invisible to it, and it stayed green
    /// with the rule it was testing removed. It asserted something true and proved nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_run_that_reported_its_ending_is_not_reported_again()
    {
        using (var store = Open())
        {
            store.Accept(Start("command-1", "run-1"));
            store.BeginRun("command-1", "run-1");
            store.Enqueue("run-1", RemoteEventKind.Running);
            store.Enqueue("run-1", RemoteEventKind.Completed, _ => "done");
        }

        using (var store = Open())
        {
            var gateway = new FakeGateway();
            var loop = Loop(store, gateway);

            loop.RecoverInterruptedRuns();
            await loop.FlushAsync();

            Assert.Equal(
                [RemoteEventKind.Running, RemoteEventKind.Completed],
                gateway.Published.Select(e => e.Kind).ToArray());
        }
    }

    // ── ordering ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sequences are allocated here, not by the caller, so two events cannot share one. A duplicate
    /// number is an event that can never be delivered: the gateway refuses anything not ahead of
    /// what it has applied.
    /// </summary>
    [Fact]
    public void Each_event_of_a_run_gets_the_next_number()
    {
        using var store = Open();
        store.Accept(Start("command-1", "run-1"));
        store.BeginRun("command-1", "run-1");

        Assert.Equal(1, store.Enqueue("run-1", RemoteEventKind.Running).Sequence);
        Assert.Equal(2, store.Enqueue("run-1", RemoteEventKind.Progress, _ => "reading").Sequence);
        Assert.Equal(3, store.Enqueue("run-1", RemoteEventKind.Completed, _ => "done").Sequence);
    }

    /// <summary>
    /// One event in flight per run, and it is the earliest. Holding one at a time is how strict
    /// order is kept without assuming anything about how the network reorders things.
    /// </summary>
    [Fact]
    public async Task Only_the_earliest_event_of_a_run_is_in_flight()
    {
        using var store = Open();
        store.Accept(Start("command-1", "run-1"));
        store.BeginRun("command-1", "run-1");
        store.Enqueue("run-1", RemoteEventKind.Running);
        store.Enqueue("run-1", RemoteEventKind.Progress, _ => "second");

        Assert.Equal(1, Assert.Single(store.NextOwed()).Sequence);

        var gateway = new FakeGateway();
        await Loop(store, gateway).FlushAsync();

        Assert.Equal([1, 2], gateway.Published.Select(e => e.Sequence).ToArray());
    }

    /// <summary>
    /// Runs do not block each other. A run that is stuck is one run, not the whole machine - which
    /// matters because the parking rule below deliberately leaves an event stuck for a while.
    /// </summary>
    [Fact]
    public void One_stuck_run_does_not_hold_up_another()
    {
        using var store = Open();
        store.Accept(Start("command-1", "run-1"));
        store.Accept(Start("command-2", "run-2"));
        store.BeginRun("command-1", "run-1");
        store.BeginRun("command-2", "run-2");
        store.Enqueue("run-1", RemoteEventKind.Running);
        store.Enqueue("run-2", RemoteEventKind.Running);

        Assert.Equal(["run-1", "run-2"], store.NextOwed().Select(o => o.RunId).Order().ToArray());
    }

    // ── what a refusal does ─────────────────────────────────────────────────

    /// <summary>Delivered means done with.</summary>
    [Fact]
    public async Task A_delivered_event_leaves_the_queue()
    {
        using var store = Open();
        Seed(store);

        await Loop(store, new FakeGateway()).FlushAsync();

        Assert.Empty(store.NextOwed());
    }

    /// <summary>
    /// A Drop code is the far end saying this is settled, so keeping the event would mean retrying
    /// something that can never succeed - for ever, and in front of every later event for that run.
    /// </summary>
    [Fact]
    public async Task An_event_refused_with_a_drop_code_is_thrown_away()
    {
        using var store = Open();
        Seed(store);

        var loop = Loop(store, new FakeGateway { Refuse = FaultCode.RunEnded });
        await loop.FlushAsync();

        Assert.Empty(store.NextOwed());
        Assert.Contains(loop.Notices, n => n.Kind == "Dropped");
    }

    /// <summary>
    /// And a transport failure is not a verdict about the event. The socket closing says nothing at
    /// all about whether the gateway would have accepted it, so it is kept.
    /// </summary>
    [Fact]
    public async Task An_event_that_failed_to_send_is_kept()
    {
        using var store = Open();
        Seed(store);

        await Loop(store, new FakeGateway { Throw = new IOException("socket closed") }).FlushAsync();

        Assert.Single(store.NextOwed());
    }

    /// <summary>
    /// A code from a newer gateway that this build has never heard of. Retried, because retrying is
    /// what never silently loses an event - and this is the case the classification table's default
    /// exists for.
    /// </summary>
    [Fact]
    public async Task An_event_refused_with_an_unknown_code_is_kept()
    {
        using var store = Open();
        Seed(store);

        await Loop(store, new FakeGateway { Refuse = "something-from-a-newer-gateway" }).FlushAsync();

        Assert.Single(store.NextOwed());
    }

    /// <summary>
    /// The limit that makes the rule above safe.
    ///
    /// <para>Per-run ordering means one stuck event blocks every later event for that run, so
    /// "retry the unknown for ever" would turn into "never deliver this run again". Past the limit
    /// the event is parked - kept and readable, no longer sent - the run proceeds, and a person is
    /// told which event stopped and why.</para>
    /// </summary>
    [Fact]
    public async Task An_event_that_cannot_be_got_rid_of_is_parked_so_the_run_can_continue()
    {
        using var store = Open();
        Seed(store);
        store.Enqueue("run-1", RemoteEventKind.Completed, _ => "done");

        var gateway = new FakeGateway { Throw = new IOException("still broken") };
        var loop = Loop(store, gateway);

        for (var attempt = 0; attempt < DeliveryLoop.MaxAttempts; attempt++)
        {
            await loop.FlushAsync();
        }

        Assert.Contains(loop.Notices, n => n.Kind == "Parked");
        Assert.Single(store.ParkedEventIds());

        // The later event is now the one owed, so the run is not stuck behind the parked one.
        gateway.Throw = null;
        await loop.FlushAsync();

        Assert.Equal(RemoteEventKind.Completed, Assert.Single(gateway.Published).Kind);
    }

    /// <summary>
    /// A revoked credential is not something a reconnection fixes. The loop stops and KEEPS the
    /// queue: what was not delivered is still there if the device is registered again.
    /// </summary>
    [Fact]
    public async Task A_fatal_refusal_stops_the_loop_and_keeps_the_queue()
    {
        using var store = Open();
        Seed(store);

        var loop = Loop(store, new FakeGateway { Refuse = FaultCode.HostRevoked });
        await loop.FlushAsync();

        Assert.True(loop.Stopped);
        Assert.Single(store.NextOwed());
        Assert.Contains(loop.Notices, n => n.Kind == "Stopped");
    }

    // ── a turn ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A command is acknowledged only after it has been written down, and a redelivery of one this
    /// machine already carried out is not handed back as work.
    /// </summary>
    [Fact]
    public async Task A_redelivered_command_is_acknowledged_but_not_handed_back_as_work()
    {
        using var store = Open();
        var command = Start("command-1", "run-1");
        var gateway = new FakeGateway { Pending = [command] };
        var loop = Loop(store, gateway);

        var first = await loop.TurnAsync([]);
        Assert.Single(first);

        store.BeginRun("command-1", "run-1");

        var second = await loop.TurnAsync([]);
        Assert.Empty(second);
        Assert.Equal(2, gateway.Acknowledged.Count);
    }

    /// <summary>An expired command is not carried out. Its run is the gateway's to write off.</summary>
    [Fact]
    public async Task An_expired_command_is_not_taken_on()
    {
        using var store = Open();
        var expired = Start("command-1", "run-1") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        var accepted = await Loop(store, new FakeGateway { Pending = [expired] }).TurnAsync([]);

        Assert.Empty(accepted);
    }

    // ── a command received is carried out, whatever interrupted it ─────────

    // Receiving a command and carrying it out are two moments, and a failed acknowledgement, a dropped
    // connection or the application closing can come between them. The gateway hands a command over
    // until it is acknowledged and never after, so a command that only a redelivery could bring back was
    // lost: acknowledged and then dropped, or written down and then met again as "not new" and thrown
    // away. A removal of a device lost so left the device trusted here, and its key never replaced.

    /// <summary>The acknowledgement failed after the command was written down; it is still carried out.</summary>
    [Fact]
    public async Task An_acknowledgement_that_fails_does_not_lose_the_command()
    {
        using var store = Open();
        var failures = 0;
        var gateway = new FakeGateway
        {
            Pending = [Start("command-1", "run-1")],
            AcknowledgeFailure = _ => failures++ == 0 ? new IOException("socket closed") : null
        };
        var started = new List<string>();
        var loop = Loop(store, gateway);
        var runner = Runner(store, started);

        await ServeAsync(loop, runner);
        await ServeAsync(loop, runner);

        Assert.Equal(["run-1"], started);
        Assert.Contains("command-1", gateway.Acknowledged);
    }

    /// <summary>
    /// Written down and acknowledged, and then the application closed. The gateway will not hand it over
    /// again, so the inbox is the only thing that still knows of it.
    /// </summary>
    [Fact]
    public async Task A_command_accepted_before_a_restart_is_applied_after_it()
    {
        using (var store = Open())
        {
            store.Accept(Start("command-1", "run-1"));
            store.MarkAcknowledged("command-1");
        }

        using (var store = Open())
        {
            var started = new List<string>();
            var loop = Loop(store, new FakeGateway());
            loop.RecoverInterruptedRuns();

            await ServeAsync(loop, Runner(store, started));

            Assert.Equal(["run-1"], started);
            Assert.True(store.WasApplied("command-1"));
        }
    }

    /// <summary>
    /// The acknowledgement of the last command of a batch fails. The ones before it were written down and
    /// acknowledged - the gateway will not hand them over again - and the one that failed was written down
    /// too; none of them is left behind.
    /// </summary>
    [Fact]
    public async Task A_failure_late_in_a_batch_does_not_lose_the_earlier_commands()
    {
        using var store = Open();
        var failed = false;
        var gateway = new FakeGateway
        {
            Pending = [Start("command-1", "run-1"), Start("command-2", "run-2"), Start("command-3", "run-3")],
            AcknowledgeFailure = id =>
            {
                if (id != "command-3" || failed) return null;
                failed = true;
                return new IOException("socket closed");
            }
        };
        var started = new List<string>();
        var loop = Loop(store, gateway);
        var runner = Runner(store, started);

        await ServeAsync(loop, runner);

        Assert.Contains("run-1", started);
        Assert.Contains("run-2", started);

        await ServeAsync(loop, runner);

        Assert.Equal(["run-1", "run-2", "run-3"], started);
    }

    /// <summary>
    /// What the inbox hands back comes in the order a Sync's commands are carried out in - a removal before
    /// a start, which would otherwise open under the key the removal replaces - and only for as long as a
    /// command of its kind may wait: a day for a start, thirty days for a removal, which waits for a computer
    /// that was off. Past that the sealer would refuse it anyway, as sealed too long ago.
    /// </summary>
    [Fact]
    public async Task What_the_inbox_owes_comes_back_removals_first_and_only_while_its_kind_may_wait()
    {
        using var store = Open();
        store.Accept(Start("command-1", "run-1"));
        store.Accept(Keys.Revoke("phone"));

        Assert.Equal(["command-r", "command-1"], (await Loop(store, new FakeGateway()).TurnAsync([])).Select(c => c.Id));

        var afterADay = DateTimeOffset.UtcNow + RemoteProtocol.CommandLifetime + TimeSpan.FromMinutes(1);
        var afterAMonth = DateTimeOffset.UtcNow + RemoteProtocol.DeviceCommandLifetime + TimeSpan.FromMinutes(1);
        Assert.Equal(["command-r"], store.Unapplied(afterADay).Select(c => c.Id));
        Assert.Empty(store.Unapplied(afterAMonth));
    }

    /// <summary>
    /// A command is carried out as it was written down. A redelivery under the same id with other text in it
    /// is the gateway changing a command after this computer received it, and what it hands back is the
    /// first.
    /// </summary>
    [Fact]
    public async Task A_redelivery_with_other_text_is_carried_out_as_first_received()
    {
        using var store = Open();
        var original = Start("command-1", "run-1");
        store.Accept(original);
        var altered = original with { Payload = Start("command-1", "run-other").Payload };

        var handed = Assert.Single(await Loop(store, new FakeGateway { Pending = [altered] }).TurnAsync([]));

        Assert.Equal(original.Payload, handed.Payload);
    }

    /// <summary>
    /// The other half: what the inbox hands back stops once it has been carried out - or refused, which is
    /// as final. A command left unmarked would be carried out on every turn, and a refused one said to the
    /// person again every fifteen seconds for as long as it lived.
    /// </summary>
    [Fact]
    public async Task A_command_carried_out_or_refused_is_not_handed_back_again()
    {
        using var store = Open();
        var forged = Keys.Command("command-f", CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload("run-1", "not sealed")));
        var loop = Loop(store, new FakeGateway { Pending = [Keys.Cancel("command-2", "run-1"), forged] });
        var runner = Runner(store, []);

        await ServeAsync(loop, runner);

        Assert.Empty(await loop.TurnAsync([]));
        Assert.Equal("Refused", Assert.Single(runner.Notices).Kind);
    }

    /// <summary>
    /// The protection the inbox exists for still holds when it hands commands back. A start that was claimed
    /// before the application closed is the business of <see cref="DeliveryLoop.RecoverInterruptedRuns"/> -
    /// reported Interrupted, never begun again - and a start handed back on two turns before its run began,
    /// the run being on a task of its own, begins once.
    /// </summary>
    [Fact]
    public async Task A_start_is_never_run_twice()
    {
        var claimed = Start("command-1", "run-1");
        var waiting = Start("command-2", "run-2");

        using (var store = Open())
        {
            store.Accept(claimed);
            store.BeginRun(claimed.Id, "run-1");
        }

        using (var store = Open())
        {
            var started = new List<string>();
            var gateway = new FakeGateway { Pending = [claimed, waiting] };
            var loop = Loop(store, gateway);
            var runner = Runner(store, started);
            loop.RecoverInterruptedRuns();

            var first = await loop.TurnAsync([]);
            var second = await loop.TurnAsync([]);
            await ApplyAsync(runner, first);
            await ApplyAsync(runner, second);
            await ServeAsync(loop, runner);

            Assert.Equal(["run-2"], started);
            Assert.Equal([RemoteEventKind.Interrupted], gateway.Published.Where(e => e.RunId == "run-1").Select(e => e.Kind));
        }
    }

    /// <summary>
    /// A removal sent from a browser is received and acknowledged, and the application closes before carrying
    /// it out. After the restart it is carried out - the device distrusted, the key replaced - and only once,
    /// however many turns follow and though the gateway hands it over again: a second rotation would send
    /// every device a key for nothing, and a second carrying out of a removal, under the key it replaced, is
    /// refused as stale and said to the person as a refusal.
    /// </summary>
    [WindowsFact]
    public async Task A_revocation_interrupted_between_receipt_and_application_rotates_exactly_once()
    {
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        HostCommand revocation;

        using (var store = Open())
        using (var keys = new HostKeyStore(store, "host-1"))
        {
            TrustDevice(keys, "phone", phone);
            TrustDevice(keys, "laptop", laptop);
            revocation = new FixedHostKeys(keys.HostId, keys.Current).Revoke("laptop");

            await DeviceLoop(store, keys, new FakeGateway { Pending = [revocation] }).TurnAsync([]);
        }

        using (var store = Open())
        using (var keys = new HostKeyStore(store, "host-1"))
        {
            var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
            var rotations = new List<KeyRotated>();
            administration.Rotated += rotations.Add;
            var loop = DeviceLoop(store, keys, new FakeGateway { Pending = [revocation] });
            var runner = DeviceRunner(store, keys, () => administration);

            for (var turn = 0; turn < 3; turn++)
            {
                await ServeAsync(loop, runner);
            }

            Assert.Equal(2u, keys.Current.Epoch);
            Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
            Assert.Single(rotations);
            Assert.Empty(runner.Notices);
            Assert.True(store.WasApplied(revocation.Id));
        }
    }

    /// <summary>
    /// A removal is recorded carried out in the very step that replaces the key. Recorded after it, a crash
    /// between the two left the removal to be carried out again - under a key it had itself replaced, so it
    /// was refused as stale and said to the person as a refused command, though it had been carried out.
    /// </summary>
    [WindowsFact]
    public async Task A_revocation_is_recorded_carried_out_in_the_step_that_rotates()
    {
        using var store = Open();
        using var keys = new HostKeyStore(store, "host-1");
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone);
        TrustDevice(keys, "laptop", laptop);
        var revocation = new FixedHostKeys(keys.HostId, keys.Current).Revoke("laptop");

        // Nothing after the key store's own step runs: the application closes the moment it commits.
        var closing = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        closing.Rotated += _ => throw new InvalidOperationException("The application closed here.");
        var administration = closing;

        var loop = DeviceLoop(store, keys, new FakeGateway { Pending = [revocation] });
        var runner = DeviceRunner(store, keys, () => administration);

        await ServeAsync(loop, runner);

        Assert.True(store.WasApplied(revocation.Id));

        administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        await ServeAsync(loop, runner);
        await ServeAsync(loop, runner);

        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Empty(runner.Notices);
    }

    // ── a start cancelled before it began, and a command that keeps failing ──

    /// <summary>
    /// The application closed with a start and the person's cancel of it both in the inbox. A start's run
    /// begins on a task of its own, and the cancel, carried out at once beside it, found no run to stop, was
    /// used up - and the task ran when the application came back, up to a day after the person cancelled it.
    /// </summary>
    [Fact]
    public async Task A_start_cancelled_before_it_began_does_not_run_after_a_restart()
    {
        using (var store = Open())
        {
            store.Accept(Start("command-1", "run-1"));
            store.Accept(Keys.Cancel("command-2", "run-1"));
        }

        using (var store = Open())
        {
            var started = new List<string>();
            var gateway = new FakeGateway();
            var loop = Loop(store, gateway);
            loop.RecoverInterruptedRuns();

            await ServeAsync(loop, Runner(store, started));
            await loop.FlushAsync();

            Assert.Empty(started);
            Assert.Equal([RemoteEventKind.Cancelled], gateway.Published.Where(e => e.RunId == "run-1").Select(e => e.Kind));
            Assert.True(store.WasApplied("command-1"));
            Assert.True(store.WasApplied("command-2"));
        }
    }

    /// <summary>
    /// The same pair in one Sync, in either order: a cancel listed after its start finds the run claimed and
    /// stops it before it begins; one listed before finds the start still in the inbox, and ends its run
    /// there and then.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_start_and_its_cancel_in_one_sync_run_nothing(bool cancelListedFirst)
    {
        using var store = Open();
        var start = Start("command-1", "run-1");
        var cancel = Keys.Cancel("command-2", "run-1");
        var started = new List<string>();
        var gateway = new FakeGateway { Pending = cancelListedFirst ? [cancel, start] : [start, cancel] };
        var loop = Loop(store, gateway);

        await ServeAsync(loop, Runner(store, started));
        await loop.FlushAsync();

        Assert.Empty(started);
        Assert.Equal([RemoteEventKind.Cancelled], gateway.Published.Where(e => e.RunId == "run-1").Select(e => e.Kind));
        Assert.True(store.WasApplied("command-1"));
        Assert.True(store.WasApplied("command-2"));
    }

    /// <summary>
    /// A command whose carrying out fails on this computer's side every time - here a start naming a run
    /// another start already opened, which fails on the run's key. Tried on every turn for its whole day, it
    /// put the same failure in the status line every fifteen seconds. It is tried <see
    /// cref="RemoteRunner.MaxAttempts"/> times, a failure is said only when it says something new, and giving
    /// up is said once.
    /// </summary>
    [Fact]
    public async Task A_command_that_keeps_failing_is_given_up_on_once()
    {
        using var store = Open();
        store.Accept(Start("command-1", "run-1"));
        store.BeginRun("command-1", "run-1");
        var loop = Loop(store, new FakeGateway { Pending = [Start("command-2", "run-1")] });
        var runner = Runner(store, []);
        var said = new List<string>();

        for (var turn = 1; turn < RemoteRunner.MaxAttempts; turn++)
        {
            await ServeAsync(loop, runner, said);
        }

        Assert.False(store.WasApplied("command-2"));
        Assert.Single(said);
        Assert.Empty(runner.Notices);

        for (var turn = 0; turn < 3; turn++)
        {
            await ServeAsync(loop, runner, said);
        }

        Assert.True(store.WasApplied("command-2"));
        Assert.Single(said);
        var gaveUp = Assert.Single(runner.Notices);
        Assert.Equal("GaveUp", gaveUp.Kind);
        Assert.Contains($"could not be carried out after {RemoteRunner.MaxAttempts} attempts", gaveUp.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure on this computer's side is not a refusal: the command is tried again on the next turn, and
    /// carried out once whatever was in the way has passed.
    /// </summary>
    [WindowsFact]
    public async Task A_command_that_failed_once_is_carried_out_on_the_next_turn()
    {
        using var store = Open();
        using var keys = new HostKeyStore(store, "host-1");
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone);
        TrustDevice(keys, "laptop", laptop);
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var asked = 0;
        var loop = DeviceLoop(store, keys, new FakeGateway { Pending = [new FixedHostKeys(keys.HostId, keys.Current).Revoke("laptop")] });
        var runner = DeviceRunner(store, keys,
            () => asked++ == 0 ? throw new InvalidOperationException("The key store was busy.") : administration);
        var said = new List<string>();

        await ServeAsync(loop, runner, said);

        Assert.Equal(1u, keys.Current.Epoch);
        Assert.Equal(["The key store was busy."], said);

        await ServeAsync(loop, runner, said);

        Assert.Equal(2u, keys.Current.Epoch);
        Assert.True(store.WasApplied("command-r"));
        Assert.Empty(runner.Notices);
    }

    /// <summary>
    /// A removal is never given up on. Given up after <see cref="RemoteRunner.MaxAttempts"/> failures - about a
    /// minute of a disk that would not write - it was marked carried out and dropped, and the device the person
    /// removed stayed trusted here with the key unchanged. It is tried for as long as it may wait, and each
    /// failure is said once.
    /// </summary>
    [WindowsFact]
    public async Task A_removal_that_keeps_failing_is_tried_until_it_succeeds()
    {
        using var store = Open();
        using var keys = new HostKeyStore(store, "host-1");
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone);
        TrustDevice(keys, "laptop", laptop);
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var asked = 0;
        var loop = DeviceLoop(store, keys, new FakeGateway { Pending = [new FixedHostKeys(keys.HostId, keys.Current).Revoke("laptop")] });
        var runner = DeviceRunner(store, keys,
            () => ++asked <= RemoteRunner.MaxAttempts ? throw new IOException("disk") : administration);
        var said = new List<string>();

        for (var turn = 0; turn <= RemoteRunner.MaxAttempts; turn++)
        {
            await ServeAsync(loop, runner, said);
        }

        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
        Assert.True(store.WasApplied("command-r"));
        Assert.Equal(["disk"], said);
        Assert.Empty(runner.Notices);
    }

    /// <summary>
    /// An endorsement is marked carried out after it, not in the same step, because carried out again it
    /// changes nothing: the application closing between the two leaves a device already trusted with that
    /// key, which the endorsement, met again, leaves as it is - and it is marked then.
    /// </summary>
    [WindowsFact]
    public async Task An_endorsement_carried_out_again_after_a_crash_changes_nothing_and_is_marked()
    {
        using var store = Open();
        using var keys = new HostKeyStore(store, "host-1");
        using var tablet = P256.Generate();
        var endorsement = new FixedHostKeys(keys.HostId, keys.Current)
            .Endorse("tablet", B64.Url(P256.PublicRaw(tablet)), "Tablet");
        store.Accept(endorsement);
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        await administration.EndorseAsync(new Sealer(keys, TimeProvider.System).OpenEndorsement(endorsement), CancellationToken.None);
        var before = Assert.Single(keys.Trusted);

        var runner = DeviceRunner(store, keys, () => administration);
        await ServeAsync(DeviceLoop(store, keys, new FakeGateway()), runner);

        Assert.Equal(before, Assert.Single(keys.Trusted), TrustedDeviceComparer.Instance);
        Assert.Empty(runner.Notices);
        Assert.True(store.WasApplied(endorsement.Id));
    }

    // ── what the inbox owes, without the gateway ───────────────────────────

    /// <summary>
    /// What the inbox owes is this computer's to carry out, and the gateway has no part in it. Handed back
    /// only after a Sync that worked, a removal already received waited for the gateway - and stayed
    /// undone exactly while the gateway could not be reached, or had refused this computer.
    /// </summary>
    [Fact]
    public async Task What_the_inbox_owes_is_handed_back_whatever_the_gateway_answers()
    {
        using var store = Open();
        store.Accept(Keys.Revoke("phone"));

        var unreachable = Loop(store, new FakeGateway { SyncRefusal = new IOException("socket closed") });
        Assert.Equal(["command-r"], unreachable.Owed().Select(c => c.Id));

        var refused = Loop(store, new FakeGateway { SyncRefusal = new GatewayRefusedException(FaultCode.HostRevoked, "revoked") });
        Assert.Equal(["command-r"], (await refused.TurnAsync([])).Select(c => c.Id));
        Assert.True(refused.Stopped);
    }

    /// <summary>
    /// A removal or an endorsement that waited out its lifetime here without being carried out - every try
    /// failed, or the computer was off for a month - can no longer be: the sealer would refuse it as too old.
    /// Dropped without a word, the person believed a device removed that this computer still trusts. It is
    /// said once, with what to do, and not looked at again.
    /// </summary>
    [Fact]
    public async Task A_device_command_that_outlived_its_lifetime_unapplied_is_said_once()
    {
        using var store = Open();
        store.Accept(Keys.Revoke("phone"));
        Backdate("command-r", RemoteProtocol.DeviceCommandLifetime + TimeSpan.FromDays(1));
        var loop = Loop(store, new FakeGateway());

        Assert.Empty(await loop.TurnAsync([]));
        Assert.Empty(await loop.TurnAsync([]));

        var notice = Assert.Single(loop.Notices);
        Assert.Equal("Expired", notice.Kind);
        Assert.Contains("was never carried out on this computer; remove the device again", notice.Detail, StringComparison.Ordinal);
        Assert.True(store.WasApplied("command-r"));
    }

    /// <summary>
    /// A database from before attempts were counted is upgraded in place, and what it holds past its lifetime
    /// is set aside without a word: that build marked nothing but a start carried out, so its old removals
    /// cannot be told from lost ones, and saying each would tell the person a device they removed was never
    /// removed here. What is still within its lifetime is handed back as anything else is.
    /// </summary>
    [Fact]
    public async Task A_database_from_before_attempts_were_counted_is_upgraded_and_its_old_rows_are_not_said()
    {
        Directory.CreateDirectory(_folder);
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ConnectionString))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE inbox (
                  command_id TEXT PRIMARY KEY, kind TEXT NOT NULL, payload TEXT NOT NULL,
                  received_at TEXT NOT NULL, acknowledged_at TEXT NULL, applied_at TEXT NULL);
                INSERT INTO inbox (command_id, kind, payload, received_at) VALUES ('old', 'RevokeDevice', '{}', $old);
                INSERT INTO inbox (command_id, kind, payload, received_at) VALUES ('new', 'RevokeDevice', '{}', $new);
                """;
            create.Parameters.AddWithValue("$old", (DateTimeOffset.UtcNow - TimeSpan.FromDays(40)).ToString("O"));
            create.Parameters.AddWithValue("$new", (DateTimeOffset.UtcNow - TimeSpan.FromDays(2)).ToString("O"));
            create.ExecuteNonQuery();
        }

        using var store = Open();
        var loop = Loop(store, new FakeGateway());

        Assert.Equal(["new"], (await loop.TurnAsync([])).Select(c => c.Id));
        Assert.Empty(loop.Notices);
        Assert.Equal((1, true), store.RecordFailure("new", "busy"));
    }

    /// <summary>Moves a command's receipt into the past, as a computer that was off for that long would have it.</summary>
    private void Backdate(string commandId, TimeSpan by)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ConnectionString);
        connection.Open();
        using var statement = connection.CreateCommand();
        statement.CommandText = "UPDATE inbox SET received_at = $at WHERE command_id = $id";
        statement.Parameters.AddWithValue("$at", (DateTimeOffset.UtcNow - by).ToString("O"));
        statement.Parameters.AddWithValue("$id", commandId);
        Assert.Equal(1, statement.ExecuteNonQuery());
    }

    /// <summary>Trusted devices compared by what is recorded about them; the key is an array, so the record's own equality will not do.</summary>
    private sealed class TrustedDeviceComparer : IEqualityComparer<TrustedDevice>
    {
        public static readonly TrustedDeviceComparer Instance = new();

        public bool Equals(TrustedDevice? x, TrustedDevice? y)
            => x is not null && y is not null && x.DeviceId == y.DeviceId && x.PublicKey.AsSpan().SequenceEqual(y.PublicKey)
               && x.Label == y.Label && x.AddedBy == y.AddedBy && x.AddedAt == y.AddedAt && x.RevokedAt == y.RevokedAt;

        public int GetHashCode(TrustedDevice obj) => obj.DeviceId.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>
    /// One turn as the service takes it: a turn of the loop, and what it handed back carried out as the
    /// service carries it out. A turn the connection dropped in is over, as it is for the service, which
    /// dials again and turns again.
    /// </summary>
    private static async Task ServeAsync(DeliveryLoop loop, RemoteRunner runner, List<string>? said = null)
    {
        IReadOnlyList<HostCommand> commands;
        try
        {
            commands = await loop.TurnAsync([]);
        }
        catch (IOException)
        {
            return;
        }

        await ApplyAsync(runner, commands, said);
    }

    /// <summary>
    /// Carries out what a turn handed back, as the service does. Each start's run is begun only once the
    /// whole batch has been gone through - a task of its own begins when it begins, and this is the latest -
    /// and waited for, so the test sees what it did. What failed is said into <paramref name="said"/>, the
    /// way the service puts it in the status line.
    /// </summary>
    private static async Task ApplyAsync(RemoteRunner runner, IReadOnlyList<HostCommand> commands, List<string>? said = null)
    {
        var runs = new List<Func<CancellationToken, Task>>();
        await runner.ApplyAllAsync(commands, runs.Add, (_, failure) => said?.Add(failure.Message));

        foreach (var run in runs)
        {
            try
            {
                await run(CancellationToken.None);
            }
            catch (Exception failure)
            {
                said?.Add(failure.Message);
            }
        }
    }

    /// <summary>A runner whose starts only say that they began: whether a run began, and how often, is what is under test.</summary>
    private static RemoteRunner Runner(HostStore store, List<string> started)
        => new(store, new RemoteApprovals(), Keys.Sealer(), (task, _, _) =>
        {
            started.Add(task.RunId);
            return Task.FromException<RemotePreparation>(new InvalidOperationException("Only the beginning is under test."));
        });

    private static DeliveryLoop DeviceLoop(HostStore store, HostKeyStore keys, IGatewayConnection gateway)
        => new(store, gateway, new Sealer(keys, TimeProvider.System), keys);

    /// <summary>A runner over the key store, as the service makes one: device commands go to the administration.</summary>
    private static RemoteRunner DeviceRunner(HostStore store, HostKeyStore keys, Func<KeyAdministration?> administration)
        => new(store, new RemoteApprovals(), new Sealer(keys, TimeProvider.System),
            (_, _, _) => throw new InvalidOperationException("No run expected"), administration);

    private static void TrustDevice(HostKeyStore keys, string deviceId, System.Security.Cryptography.ECDiffieHellman device)
        => keys.Trust(new TrustedDevice(deviceId, P256.PublicRaw(device), deviceId, "test", DateTimeOffset.UtcNow, null));

    // ── a busy account ──────────────────────────────────────────────────────

    /// <summary>
    /// An account over its rate is told to wait, and waiting is what the Host does. Counting the
    /// refusal toward parking would park a perfectly good event after ten refusals in a busy minute,
    /// and every later event of its run would go out with a hole in front of it.
    /// </summary>
    [Fact]
    public async Task A_quota_refusal_waits_and_does_not_count_toward_parking()
    {
        using var store = Open();
        Seed(store);

        var loop = Loop(store, new FakeGateway { Refuse = FaultCode.QuotaExceeded });

        for (var attempt = 0; attempt < DeliveryLoop.MaxAttempts + 2; attempt++)
        {
            await loop.FlushAsync();
        }

        Assert.DoesNotContain(loop.Notices, n => n.Kind == "Parked");
        Assert.Empty(store.ParkedEventIds());
        Assert.Equal(0, Assert.Single(store.NextOwed()).Attempts);
    }

    /// <summary>
    /// The same for Sync. A refused Sync used to escape the turn as an exception, which the service
    /// treats as a dropped connection: it tore the connection down and dialled again, which is more
    /// calls against the very limit that refused it.
    ///
    /// <para>What the inbox still owes is handed back all the same: carrying it out needs nothing from
    /// the gateway, so it does not wait with the gateway's next answer.</para>
    /// </summary>
    [Fact]
    public async Task A_sync_refused_for_quota_is_a_wait_not_a_dropped_connection()
    {
        using var store = Open();
        store.Accept(Start("command-1", "run-1"));
        var loop = Loop(store, new FakeGateway
        {
            SyncRefusal = new GatewayRefusedException(FaultCode.QuotaExceeded, "slow down")
        });

        Assert.Equal(["command-1"], (await loop.TurnAsync([])).Select(c => c.Id));
        Assert.False(loop.Stopped);
    }

    /// <summary>
    /// A Sync refused for good - the credential gone - stops the loop with the reason, rather than
    /// sending the service round its reconnect loop with a credential that will be refused again.
    /// </summary>
    [Fact]
    public async Task A_sync_refused_for_good_stops_the_loop()
    {
        using var store = Open();
        var loop = Loop(store, new FakeGateway
        {
            SyncRefusal = new GatewayRefusedException(FaultCode.HostRevoked, "revoked")
        });

        Assert.Empty(await loop.TurnAsync([]));
        Assert.True(loop.Stopped);
        Assert.Contains(loop.Notices, n => n.Kind == "Stopped" && n.Detail.Contains("revoked", StringComparison.Ordinal));
    }

    // ── grants owed ─────────────────────────────────────────────────────────

    /// <summary>
    /// Grants go before events. A device can read an event only with the key a grant gives it, so an
    /// event that arrived first would sit on the phone as something it cannot open.
    /// </summary>
    [Fact]
    public async Task Grants_go_before_events_and_leave_the_outbox()
    {
        using var store = Open();
        Seed(store);
        var outbox = new GrantOutbox(Grant("phone"));
        var gateway = new FakeGateway();

        await Loop(store, gateway, outbox).TurnAsync([]);

        Assert.Equal(["PublishGrants", "Publish", "Sync"], gateway.Calls);
        Assert.Equal("phone", Assert.Single(Assert.Single(gateway.GrantCalls)).DeviceId);
        Assert.Empty(outbox.PendingGrants());
    }

    /// <summary>The gateway takes at most fifty grants a call and refuses the whole call past that.</summary>
    [Fact]
    public async Task Grants_are_sent_fifty_at_a_time()
    {
        using var store = Open();
        var outbox = new GrantOutbox([.. Enumerable.Range(0, 120).Select(i => Grant("device-" + i))]);
        var gateway = new FakeGateway();

        await Loop(store, gateway, outbox).TurnAsync([]);

        Assert.Equal([50, 50, 20], gateway.GrantCalls.Select(call => call.Count));
        Assert.Empty(outbox.PendingGrants());
    }

    /// <summary>
    /// A grant the gateway refuses as malformed, or for a device it no longer has, cannot become
    /// acceptable by being sent again. It is discarded and said, and it does not take the grants
    /// that were in the same call down with it: the gateway refuses a call whole.
    /// </summary>
    [Theory]
    [InlineData(FaultCode.BadGrant)]
    [InlineData("not-found")]
    public async Task A_grant_the_gateway_refuses_is_discarded_and_said(string code)
    {
        using var store = Open();
        var outbox = new GrantOutbox(Grant("phone"), Grant("removed"), Grant("laptop"));
        var gateway = new FakeGateway
        {
            GrantRefusal = grants => grants.Any(g => g.DeviceId == "removed")
                ? new GatewayRefusedException(code, "That device was removed from the account.")
                : null
        };
        var loop = Loop(store, gateway, outbox);

        await loop.TurnAsync([]);

        Assert.Equal(["laptop", "phone"], gateway.GrantCalls.SelectMany(call => call).Select(g => g.DeviceId).Order());
        Assert.Empty(outbox.PendingGrants());
        Assert.Contains(loop.Notices, n => n.Kind == "GrantDropped" && n.Detail.Contains("removed", StringComparison.Ordinal));
        Assert.False(loop.Stopped);
    }

    /// <summary>A grant refused for the account's rate is kept, for the next turn.</summary>
    [Fact]
    public async Task A_grant_refused_for_quota_is_kept_for_the_next_turn()
    {
        using var store = Open();
        var outbox = new GrantOutbox(Grant("phone"));
        var gateway = new FakeGateway
        {
            GrantRefusal = _ => new GatewayRefusedException(FaultCode.QuotaExceeded, "slow down")
        };
        var loop = Loop(store, gateway, outbox);

        await loop.TurnAsync([]);

        Assert.Single(outbox.PendingGrants());
        Assert.DoesNotContain(loop.Notices, n => n.Kind == "GrantDropped");

        gateway.GrantRefusal = null;
        await loop.TurnAsync([]);

        Assert.Empty(outbox.PendingGrants());
    }

    private static DeliveryLoop Loop(HostStore store, IGatewayConnection gateway, IGrantOutbox grants)
        => new(store, gateway, Keys.Sealer(), grants);

    private static KeyGrant Grant(string deviceId)
        => new("host-1", deviceId, 1, "ephemeral", "nonce", "ciphertext", "pair:connect", "signing", "mac");

    /// <summary>The key store's outbox of grants, in memory: what is under test is what the loop does with it.</summary>
    private sealed class GrantOutbox(params KeyGrant[] grants) : IGrantOutbox
    {
        private readonly List<PendingGrant> _pending =
            [.. grants.Select(g => new PendingGrant($"{g.HostId}:{g.DeviceId}:{g.Epoch}", g))];

        public IReadOnlyList<PendingGrant> PendingGrants() => [.. _pending];

        public void DiscardGrant(string id) => _pending.RemoveAll(p => p.Id == id);
    }

    private static void Seed(HostStore store)
    {
        store.Accept(Start("command-1", "run-1"));
        store.BeginRun("command-1", "run-1");
        store.Enqueue("run-1", RemoteEventKind.Running);
    }
}
