namespace Enactive.Engine.Tests;

using Enactive.Remote.Contracts;
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

    private static HostCommand Start(string commandId, string runId)
        => new(commandId, "host-1", CommandKind.StartTask,
            RemoteJson.Serialize(new StartTaskPayload(
                runId, "task-1", "workspace-1", "Run the tests", "Please.", DateTimeOffset.UtcNow)),
            CommandStatus.PendingDelivery,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24));

    // ── accepting a command exactly once ────────────────────────────────────

    /// <summary>
    /// The reported shape of at-least-once delivery: the same command arrives twice because the
    /// first acknowledgement was lost. It must be recognised across a RESTART, not merely within
    /// one process - the gap between accepting and acknowledging is exactly where a crash lands.
    /// </summary>
    [Fact]
    public void A_redelivered_command_is_recognised_after_a_restart()
    {
        var command = Start("command-1", "run-1");

        using (var store = Open())
        {
            Assert.True(store.Accept(command));
        }

        using (var store = Open())
        {
            Assert.False(store.Accept(command));
        }
    }

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

            var loop = new DeliveryLoop(store, new FakeGateway());
            loop.RecoverInterruptedRuns();

            var owed = Assert.Single(store.NextOwed());
            Assert.Equal(RemoteEventKind.Interrupted, owed.Event.Kind);
            Assert.Equal("run-1", owed.RunId);
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
            store.Enqueue("run-1", RemoteEventKind.Completed, "done");
        }

        using (var store = Open())
        {
            var gateway = new FakeGateway();
            var loop = new DeliveryLoop(store, gateway);

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
        Assert.Equal(2, store.Enqueue("run-1", RemoteEventKind.Progress, "reading").Sequence);
        Assert.Equal(3, store.Enqueue("run-1", RemoteEventKind.Completed, "done").Sequence);
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
        store.Enqueue("run-1", RemoteEventKind.Progress, "second");

        Assert.Equal(1, Assert.Single(store.NextOwed()).Sequence);

        var gateway = new FakeGateway();
        await new DeliveryLoop(store, gateway).FlushAsync();

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

        await new DeliveryLoop(store, new FakeGateway()).FlushAsync();

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

        var loop = new DeliveryLoop(store, new FakeGateway { Refuse = FaultCode.RunEnded });
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

        await new DeliveryLoop(store, new FakeGateway { Throw = new IOException("socket closed") }).FlushAsync();

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

        await new DeliveryLoop(store, new FakeGateway { Refuse = "something-from-a-newer-gateway" }).FlushAsync();

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
        store.Enqueue("run-1", RemoteEventKind.Completed, "done");

        var gateway = new FakeGateway { Throw = new IOException("still broken") };
        var loop = new DeliveryLoop(store, gateway);

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

        var loop = new DeliveryLoop(store, new FakeGateway { Refuse = FaultCode.HostRevoked });
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
        var loop = new DeliveryLoop(store, gateway);

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

        var accepted = await new DeliveryLoop(store, new FakeGateway { Pending = [expired] }).TurnAsync([]);

        Assert.Empty(accepted);
    }

    private static void Seed(HostStore store)
    {
        store.Accept(Start("command-1", "run-1"));
        store.BeginRun("command-1", "run-1");
        store.Enqueue("run-1", RemoteEventKind.Running);
    }

    /// <summary>The far end, doing whatever the test needs it to.</summary>
    private sealed class FakeGateway : IGatewayConnection
    {
        public List<HostCommand> Pending { get; init; } = [];

        public List<HostEvent> Published { get; } = [];

        public List<string> Acknowledged { get; } = [];

        /// <summary>A coded refusal, as the gateway would send it.</summary>
        public string? Refuse { get; set; }

        /// <summary>Something that is not a refusal at all - a closed socket, a timeout.</summary>
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<HostCommand>> SyncAsync(
            IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HostCommand>>(Pending);

        public Task AcknowledgeAsync(string commandId, CancellationToken ct)
        {
            Acknowledged.Add(commandId);
            return Task.CompletedTask;
        }

        public Task PublishAsync(HostEvent published, CancellationToken ct)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Refuse is not null)
            {
                throw new GatewayRefusedException(Refuse, "refused by the fake gateway");
            }

            Published.Add(published);
            return Task.CompletedTask;
        }
    }
}
