namespace Enactive.Remote.Gateway.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Xunit;

/// <summary>
/// What a person's panel is shown, and what their poll's cursor promises - each person's own.
///
/// <para>Two questions run through these. Can the panel trust that "everything since N" means
/// everything: a poll that loses a row loses it once, silently, for ever, and for a permission
/// request that is a question nobody is asked. And is everything it is shown its owner's: another
/// person's rows must neither appear in the snapshot nor crowd the owner's own out of it.</para>
///
/// <para>Every test makes its own people, so the assertions are about their rows and not about how
/// many the shared database holds.</para>
/// </summary>
public sealed class ProjectionTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);

    private HostService Host => new(Db, Limits.Unlimited);

    private UserService Users => new(Db, Limits.Unlimited, TimeProvider.System);

    private Retention Trimmer => new(Db, days: 30);

    private Projection Panel => new(Db, Trimmer);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private Task<UserAccess> PersonAsync(string stem) => TestAccounts.CreateAsync(database, stem + NewId()[..8]);

    /// <summary>A field as the Host seals it. The gateway checks only the shape, so any key will do.</summary>
    private static string Sealed(string text)
        => Envelope.Seal(RandomNumberGenerator.GetBytes(32), 1, Encoding.UTF8.GetBytes(text), []);

    private Task<GatewaySnapshot> ReadAsync(UserAccess user, string? since = null)
        => Panel.ReadAsync(user, since, default);

    /// <summary>The ordinal a cursor carries, for the calls that take a number from the panel's screen.</summary>
    private static long OrdinalOf(string cursor)
        => long.Parse(cursor[(cursor.IndexOf('.') + 1)..], CultureInfo.InvariantCulture);

    /// <summary>The epoch a cursor carries.</summary>
    private static int EpochOf(string cursor)
        => int.Parse(cursor[..cursor.IndexOf('.')], CultureInfo.InvariantCulture);

    /// <summary>A registered computer of <paramref name="user"/>'s that has synced one workspace.</summary>
    private async Task<(HostAccess Host, string Token, string SealedName)> ComputerAsync(
        UserAccess user, string label = "Studio PC")
    {
        var (id, _, token) = await Users.RegisterHostAsync(user, label, default);
        var host = new HostAccess(id, user.UserId);
        var name = Sealed("Enactive");

        await Host.SyncAsync(host, [new WorkspaceRef("workspace-1", name)]);
        return (host, token, name);
    }

    /// <summary>A task and a queued run of it, as a person's start leaves them.</summary>
    private async Task<string> QueuedRunAsync(HostAccess host)
    {
        var taskId = Guid.NewGuid().ToString();
        var runId = NewId();

        await database.ExecuteAsync(
            """
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
              VALUES (@owner, @task, @host, 'workspace-1', @sealed, SHA2(@task, 256), UTC_TIMESTAMP(3));
            INSERT INTO runs (id, owner_id, task_id, host_id, status, applied_sequence, created_at)
              VALUES (@run, @owner, @task, @host, 'Queued', 0, UTC_TIMESTAMP(3));
            """,
            ("@owner", host.OwnerId), ("@task", taskId), ("@host", host.HostId),
            ("@sealed", Sealed("Run the tests")), ("@run", runId));

        return runId;
    }

    /// <summary>A fresh person with a computer and a run it has reported started: one ordinal taken.</summary>
    private async Task<(UserAccess Person, HostAccess Host, string RunId)> RunningAsync(string stem)
    {
        var person = await PersonAsync(stem);
        var (host, _, _) = await ComputerAsync(person);
        var runId = await QueuedRunAsync(host);

        await Host.PublishAsync(host, Event(runId, 1, RemoteEventKind.Running));
        return (person, host, runId);
    }

    private static HostEvent Event(
        string runId, long sequence, RemoteEventKind kind, string? sealedDetail = null,
        ApprovalRequest? approval = null)
        => new(NewId(), runId, sequence, kind, sealedDetail, approval);

    /// <summary>
    /// Events written straight to the table, taking a block of the owner's ordinals in one go. A
    /// fixture and not the write path: <see cref="HostService"/> takes them one at a time under the
    /// lock. What matters is that the rows exist and the owner's counter is not left behind them,
    /// because a counter behind a row would deliver it again on every poll.
    /// </summary>
    private async Task BackfillEventsAsync(HostAccess host, string runId, int count, long firstSequence = 2)
    {
        await using var connection = await database.OpenAsync();
        await using var transaction = await connection.BeginAsync(default);

        await connection.ExecuteAsync(transaction,
            "UPDATE user_streams SET value = value + @count WHERE owner_id = @owner",
            ("@count", count), ("@owner", host.OwnerId));

        var last = await connection.ReadOneAsync(transaction,
            "SELECT value FROM user_streams WHERE owner_id = @owner",
            reader => reader.GetInt64("value"), ("@owner", host.OwnerId));

        var rows = Enumerable.Range(0, count).Select(i =>
            $"('{host.OwnerId}', '{host.HostId}', '{NewId()}', '{runId}', {firstSequence + i}, 'Progress', "
            + $"NULL, UTC_TIMESTAMP(3), {last - count + 1 + i})");

        await connection.ExecuteAsync(transaction,
            "INSERT INTO events (owner_id, host_id, id, run_id, sequence, kind, sealed_detail, at, ordinal) VALUES "
            + string.Join(",", rows));

        await transaction.CommitAsync();
    }

    // ── whose rows ──────────────────────────────────────────────────────────

    /// <summary>
    /// The owner filter is applied before the LIMIT, not after it.
    ///
    /// <para>The newest 200 rows of the whole table, filtered to Alice's afterwards, are 200 of
    /// Bob's and none of hers: a busy neighbour would empty her panel. Shown red by taking the
    /// newest rows of everybody's and keeping the owner's - Alice's run, task and event are then
    /// missing - or by not filtering at all, which shows her Bob's.</para>
    /// </summary>
    [Fact]
    public async Task Bobs_250_newest_runs_do_not_crowd_out_alices_runs()
    {
        var (alice, alicesHost, alicesRun) = await RunningAsync("alice");
        var (bob, bobsHost, bobsRun) = await RunningAsync("bob");

        // Bob's are all newer than Alice's: a newest-first LIMIT over the whole table reaches his first.
        var bobsTasks = Enumerable.Range(0, 250).Select(_ => Guid.NewGuid().ToString()).ToArray();
        await database.ExecuteAsync(
            "INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at) VALUES "
            + string.Join(",", bobsTasks.Select((task, i) =>
                $"('{bob.UserId}', '{task}', '{bobsHost.HostId}', 'workspace-1', '{Sealed("Bob's")}', "
                + $"SHA2('{task}', 256), UTC_TIMESTAMP(3) + INTERVAL {i + 1} SECOND)"))
            + ";"
            + "INSERT INTO runs (id, owner_id, task_id, host_id, status, applied_sequence, created_at) VALUES "
            + string.Join(",", bobsTasks.Select((task, i) =>
                $"('{NewId()}', '{bob.UserId}', '{task}', '{bobsHost.HostId}', 'Queued', 0, "
                + $"UTC_TIMESTAMP(3) + INTERVAL {i + 1} SECOND)")));
        await BackfillEventsAsync(bobsHost, bobsRun, count: 250);

        var alices = await ReadAsync(alice);

        Assert.Equal(alicesRun, Assert.Single(alices.Runs).Id);
        Assert.Single(alices.Tasks);
        Assert.Equal(alicesRun, Assert.Single(alices.Events).RunId);
        Assert.Equal(alicesHost.HostId, Assert.Single(alices.Hosts).Id);

        var bobs = await ReadAsync(bob);

        Assert.Equal(200, bobs.Runs.Count);
        Assert.Equal(200, bobs.Tasks.Count);
        Assert.Equal(200, bobs.Events.Count);
        Assert.All(bobs.Runs, run => Assert.Equal(bobsHost.HostId, run.HostId));
    }

    /// <summary>
    /// Nothing of another person's is in a snapshot: not their computers, tasks, runs, requests,
    /// events or notices, and not their unread count. Alice has nothing at all, so every collection
    /// of hers must be empty - whatever Bob's computer is doing.
    ///
    /// <para>Shown red by dropping the owner filter from any one of the reads.</para>
    /// </summary>
    [Fact]
    public async Task Alices_snapshot_holds_nothing_of_bobs()
    {
        var alice = await PersonAsync("alice");
        var (_, bobsHost, bobsRun) = await RunningAsync("bob");
        await Host.PublishAsync(bobsHost, Event(bobsRun, 2, RemoteEventKind.ApprovalRequested, Sealed("Delete"),
            new ApprovalRequest(NewId(), "call-1", "hash-1", true, Sealed("rm -rf build"))));

        var first = await ReadAsync(alice);

        Assert.Empty(first.Hosts);
        Assert.Empty(first.Tasks);
        Assert.Empty(first.Runs);
        Assert.Empty(first.Approvals);
        Assert.Empty(first.Events);
        Assert.Empty(first.Notices);
        Assert.Equal(0, first.UnreadNotices);
        Assert.Equal("1.0", first.Cursor);

        // And Bob's next event does not reach her delta either.
        await Host.PublishAsync(bobsHost, Event(bobsRun, 3, RemoteEventKind.Progress, Sealed("still going")));

        var delta = await ReadAsync(alice, first.Cursor);

        Assert.True(delta.Delta);
        Assert.Empty(delta.Events);
        Assert.Empty(delta.Notices);
        Assert.Equal("1.0", delta.Cursor);
    }

    // ── the cursor ──────────────────────────────────────────────────────────

    /// <summary>
    /// A poll that carries the cursor it was given is answered with what happened since, and the
    /// next one with nothing. The cheap half: a delta that re-sent the last row on every poll would
    /// look right to every other test here while costing what the delta was written to stop.
    /// </summary>
    [Fact]
    public async Task A_poll_with_a_cursor_is_answered_as_a_delta()
    {
        var (alice, host, runId) = await RunningAsync("alice");

        var first = await ReadAsync(alice);
        Assert.False(first.Delta);

        await Host.PublishAsync(host, Event(runId, 2, RemoteEventKind.Progress, Sealed("reading")));

        var second = await ReadAsync(alice, first.Cursor);
        Assert.True(second.Delta);
        Assert.Equal(2, Assert.Single(second.Events).Sequence);
        Assert.Single(second.Runs, run => run.Id == runId);

        var third = await ReadAsync(alice, second.Cursor);
        Assert.True(third.Delta);
        Assert.Empty(third.Events);
        Assert.Empty(third.Notices);
    }

    /// <summary>
    /// The one the line exists for. A publish is in flight - its ordinal taken and its row written,
    /// not committed - and the panel polls in that moment. A cursor covering that row would lose it:
    /// by the time it commits, the panel is asking for something higher.
    ///
    /// <para>Shown red by reading the cursor as MAX(ordinal) under READ UNCOMMITTED, or from the
    /// counter outside the snapshot's transaction.</para>
    /// </summary>
    [Fact]
    public async Task A_cursor_never_covers_a_row_that_has_not_committed()
    {
        var (alice, host, runId) = await RunningAsync("alice");
        var detail = Sealed("in flight");

        await using var connection = await database.OpenAsync();
        await using var inFlight = await connection.BeginAsync(default);

        var ordinal = await StreamCursor.NextAsync(connection, inFlight, alice.UserId);
        await connection.ExecuteAsync(inFlight,
            """
            INSERT INTO events (owner_id, host_id, id, run_id, sequence, kind, sealed_detail, at, ordinal)
            VALUES (@owner, @host, @id, @run, 2, 'Progress', @detail, UTC_TIMESTAMP(3), @ordinal)
            """,
            ("@owner", alice.UserId), ("@host", host.HostId), ("@id", NewId()), ("@run", runId),
            ("@detail", detail), ("@ordinal", ordinal));

        var during = await ReadAsync(alice);
        Assert.True(OrdinalOf(during.Cursor) < ordinal,
            $"The cursor was {during.Cursor} while ordinal {ordinal} was still uncommitted.");

        await inFlight.CommitAsync();

        var afterwards = await ReadAsync(alice, during.Cursor);
        Assert.True(afterwards.Delta);
        Assert.Contains(afterwards.Events, e => e.SealedDetail == detail);
    }

    /// <summary>
    /// A panel closed for a while asks for more than a poll may carry. Truncating the answer would
    /// hand back a cursor covering rows that were never sent, and the panel would never ask for them
    /// again. So the reply is a full snapshot, and says so.
    ///
    /// <para>Shown red by capping the delta with a LIMIT and returning it as a delta anyway.</para>
    /// </summary>
    [Fact]
    public async Task An_oversized_delta_becomes_a_full_snapshot()
    {
        var (alice, host, runId) = await RunningAsync("alice");
        var start = (await ReadAsync(alice)).Cursor;

        await BackfillEventsAsync(host, runId, count: 250);

        var reply = await ReadAsync(alice, start);

        Assert.False(reply.Delta);
        Assert.Equal(200, reply.Events.Count);
        Assert.Equal(251, reply.Events[^1].Sequence);
        Assert.Equal(reply.Events.Select(e => e.Ordinal).Order(), reply.Events.Select(e => e.Ordinal));
        Assert.True(OrdinalOf(reply.Cursor) >= reply.Events.Max(e => e.Ordinal));
    }

    /// <summary>
    /// The line carries an epoch so that resetting it - after a restore, say - invalidates every
    /// browser's cursor at once. A cursor of another epoch numbers rows that are not this line's,
    /// so it is answered with the whole snapshot rather than a delta from a number that means
    /// something else now.
    ///
    /// <para>Shown red by comparing only the ordinal: both cursors below are then answered as
    /// deltas, and the first one would hide every row numbered at or below 1 on the new line.</para>
    /// </summary>
    [Fact]
    public async Task A_cursor_from_another_epoch_gives_a_full_snapshot()
    {
        var (alice, _, runId) = await RunningAsync("alice");
        var first = await ReadAsync(alice);
        Assert.Equal("1.1", first.Cursor);

        // A cursor naming an epoch the line has never had.
        var forged = await ReadAsync(alice, "2.1");
        Assert.False(forged.Delta);
        Assert.Contains(forged.Events, e => e.RunId == runId);

        // The line is reset: the browser's cursor is from the epoch before.
        await database.ExecuteAsync($"UPDATE user_streams SET epoch = 2 WHERE owner_id = '{alice.UserId}'");

        var reset = await ReadAsync(alice, first.Cursor);
        Assert.False(reset.Delta);
        Assert.Contains(reset.Events, e => e.RunId == runId);
        Assert.Equal("2.1", reset.Cursor);

        Assert.True((await ReadAsync(alice, reset.Cursor)).Delta);
    }

    /// <summary>
    /// A cursor above the line's value names rows that do not exist yet. Answered as a delta, it
    /// would hide every row numbered up to it when they did - a restored database whose line is
    /// behind the browser's would never show the next events at all. It gets the whole snapshot.
    ///
    /// <para>Shown red by not comparing the cursor with the line's value: the reply is then an
    /// empty delta.</para>
    /// </summary>
    [Fact]
    public async Task A_cursor_above_the_current_value_gives_a_full_snapshot()
    {
        var (alice, _, runId) = await RunningAsync("alice");
        var first = await ReadAsync(alice);

        var reply = await ReadAsync(alice, $"1.{OrdinalOf(first.Cursor) + 1}");

        Assert.False(reply.Delta);
        Assert.Contains(reply.Events, e => e.RunId == runId);
        Assert.Equal(first.Cursor, reply.Cursor);
    }

    /// <summary>
    /// Anything that is not "{epoch}.{ordinal}" in plain digits gets the person's whole snapshot.
    /// Never an error the panel cannot act on, and never a delta from a number guessed out of it.
    ///
    /// <para>The last case is a valid cursor padded beyond the bound: an input that long is not
    /// parsed at all. Shown red by removing the bound.</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("1")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1..1")]
    [InlineData("1.1.1")]
    [InlineData("-1.1")]
    [InlineData("1.-1")]
    [InlineData("+1.1")]
    [InlineData(" 1.1")]
    [InlineData("1.1 ")]
    [InlineData("1,1")]
    [InlineData("1.1e0")]
    [InlineData("1.99999999999999999999")]
    [InlineData("99999999999.1")]
    [InlineData("1.00000000000000000000000000000000000000001")]
    public async Task A_malformed_cursor_gives_a_full_snapshot(string cursor)
    {
        var (alice, _, runId) = await RunningAsync("alice");

        var reply = await ReadAsync(alice, cursor);

        Assert.False(reply.Delta);
        Assert.Contains(reply.Events, e => e.RunId == runId);
        Assert.Equal("1.1", reply.Cursor);
    }

    /// <summary>
    /// Read state is not in the stream. Marking notices read UPDATES rows the panel already has,
    /// and an append-only delta cannot carry an update - so the count comes back whole every time.
    ///
    /// <para>Shown red by counting the unread notices in the delta instead of in the table: the
    /// middle poll carries no notices, and a count taken from it says zero while a notice sits
    /// unread on the screen before it.</para>
    /// </summary>
    [Fact]
    public async Task An_empty_delta_still_reports_the_notices_that_are_unread()
    {
        var (alice, host, runId) = await RunningAsync("alice");
        await Host.PublishAsync(host, Event(runId, 2, RemoteEventKind.Completed, Sealed("done")));

        var first = await ReadAsync(alice);
        Assert.Equal(1, first.UnreadNotices);

        var quiet = await ReadAsync(alice, first.Cursor);
        Assert.True(quiet.Delta);
        Assert.Empty(quiet.Notices);
        Assert.Equal(1, quiet.UnreadNotices);

        await Users.MarkNoticesReadAsync(alice, EpochOf(quiet.Cursor), OrdinalOf(quiet.Cursor), default);

        var afterReading = await ReadAsync(alice, quiet.Cursor);
        Assert.Empty(afterReading.Notices);
        Assert.Equal(0, afterReading.UnreadNotices);
    }

    // ── what the panel is shown ─────────────────────────────────────────────

    /// <summary>
    /// The panel opens what it is shown, so it is shown what opening needs: a sealed field together
    /// with the computer whose key sealed it, and the sequence and kind its associated data was built
    /// from. A notice has no computer of its own - it is read from the notice's run.
    /// </summary>
    [Fact]
    public async Task A_finished_run_and_its_notice_carry_what_the_panel_needs_to_open_them()
    {
        var (alice, host, runId) = await RunningAsync("alice");
        var done = Sealed("All done");
        await Host.PublishAsync(host, Event(runId, 2, RemoteEventKind.Completed, done));

        var state = await ReadAsync(alice);

        var run = Assert.Single(state.Runs);
        Assert.Equal(RemoteRunStatus.Completed, run.Status);
        Assert.Equal(done, run.SealedSummary);
        Assert.Equal(2, run.SummarySequence);

        var notice = Assert.Single(state.Notices);
        Assert.Equal(host.HostId, notice.HostId);
        Assert.Equal(runId, notice.RunId);
        Assert.Equal("Completed", notice.Kind);
        Assert.Equal(done, notice.SealedDetail);
        Assert.Equal(2, notice.EventSequence);
        Assert.Equal(RemoteEventKind.Completed, notice.EventKind);
        Assert.False(notice.Read);

        Assert.All(state.Events, e => Assert.Equal(host.HostId, e.HostId));
    }

    /// <summary>
    /// No credential, in any shape, ever reaches the panel. The row types carry a token hash and the
    /// view types do not, so leaking one means adding a field on purpose - but a projection is
    /// exactly where somebody adds a field on purpose, so it is asserted.
    /// </summary>
    [Fact]
    public async Task Nothing_secret_reaches_the_panel()
    {
        var alice = await PersonAsync("alice");
        var (_, token, _) = await ComputerAsync(alice);

        var json = RemoteJson.Serialize(await ReadAsync(alice));

        Assert.DoesNotContain(token, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Ids.Hash(token), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token_hash", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A permission the panel must render as an explanation and not as a button. The flag travels so
    /// the panel can say WHY; the refusal in <see cref="UserService"/> is what actually holds. The
    /// action travels sealed, whole, with the computer whose key opens it.
    /// </summary>
    [Fact]
    public async Task The_panel_is_told_which_permissions_it_may_not_answer()
    {
        var (alice, host, runId) = await RunningAsync("alice");
        var approvalId = NewId();
        var action = Sealed("dotnet test");

        await Host.PublishAsync(host, Event(runId, 2, RemoteEventKind.ApprovalRequested, Sealed("Run the suite"),
            new ApprovalRequest(approvalId, "call-1", "hash-1", RemoteDecidable: false, action)));

        var pending = Assert.Single((await ReadAsync(alice)).Approvals);

        Assert.Equal(approvalId, pending.Id);
        Assert.False(pending.RemoteDecidable);
        Assert.Equal(action, pending.SealedAction);
        Assert.Equal(host.HostId, pending.HostId);
        Assert.Equal(runId, pending.RunId);
        Assert.Equal("call-1", pending.ToolCallId);
        Assert.Equal("hash-1", pending.ActionHash);
        Assert.Equal(ApprovalStatus.Pending, pending.Status);
    }

    /// <summary>A computer that has not synced recently is shown as offline rather than as ready.</summary>
    [Fact]
    public async Task A_computer_that_stopped_syncing_is_shown_as_offline()
    {
        var alice = await PersonAsync("alice");
        var (host, _, name) = await ComputerAsync(alice, "Quiet PC");

        var shown = Assert.Single((await ReadAsync(alice)).Hosts);
        Assert.True(shown.Online);
        Assert.Equal("Quiet PC", shown.Label);
        Assert.Equal(name, Assert.Single(shown.Workspaces).SealedName);

        await database.ExecuteAsync(
            $"UPDATE hosts SET last_seen_at = UTC_TIMESTAMP(3) - INTERVAL 5 MINUTE WHERE id = '{host.HostId}'");

        Assert.False(Assert.Single((await ReadAsync(alice)).Hosts).Online);
    }

    // ── retention ───────────────────────────────────────────────────────────

    /// <summary>
    /// When history IS discarded the panel is told, so a short list reads as a trimmed history
    /// rather than as a quiet fortnight. The line is not rewound by it: a cursor from before the
    /// trim is still a cursor of this line, and numbers are never handed out twice.
    /// </summary>
    [Fact]
    public async Task History_past_the_window_is_deleted_and_the_panel_is_told()
    {
        var (alice, host, runId) = await RunningAsync("alice");
        await Host.PublishAsync(host, Event(runId, 2, RemoteEventKind.Completed, Sealed("long ago")));
        var before = await ReadAsync(alice);

        await database.ExecuteAsync(
            $"""
            UPDATE events SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE owner_id = '{alice.UserId}';
            UPDATE notices SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE owner_id = '{alice.UserId}';
            """);

        Assert.True(await Trimmer.TrimAsync() >= 3);

        var state = await ReadAsync(alice);
        Assert.Empty(state.Events);
        Assert.Empty(state.Notices);
        Assert.NotNull(state.Retention.TrimmedBefore);
        Assert.Equal(30, state.Retention.Days);
        Assert.Equal(before.Cursor, state.Cursor);
        Assert.True((await ReadAsync(alice, before.Cursor)).Delta);
    }

    /// <summary>
    /// Nothing of hers was old enough, so nothing is announced to her - whatever the same pass did
    /// to anybody else. A panel that said history before last month had been trimmed would be
    /// describing data that never existed.
    ///
    /// <para>Shown red by stamping the cutoff on every pass, or on every account when any was
    /// trimmed.</para>
    /// </summary>
    [Fact]
    public async Task A_pass_that_deletes_nothing_of_hers_says_nothing_was_trimmed()
    {
        var (alice, _, runId) = await RunningAsync("alice");

        await Trimmer.TrimAsync();

        var state = await ReadAsync(alice);
        Assert.Null(state.Retention.TrimmedBefore);
        Assert.Contains(state.Events, e => e.RunId == runId);
    }

    /// <summary>
    /// The marker is each person's. One pass trims everybody whose history is past the window, and
    /// writes the marker of each person it took something from - and of nobody else.
    ///
    /// <para>Shown red by keeping one marker for the gateway (Alice is then told her history was
    /// trimmed when nothing of hers was), or by deleting without the owner in the statement.</para>
    /// </summary>
    [Fact]
    public async Task Trimming_bobs_notices_leaves_alices_marker()
    {
        var (alice, alicesHost, alicesRun) = await RunningAsync("alice");
        var (bob, bobsHost, bobsRun) = await RunningAsync("bob");
        var (carol, _, _) = await RunningAsync("carol");
        await Host.PublishAsync(alicesHost, Event(alicesRun, 2, RemoteEventKind.Completed, Sealed("hers")));
        await Host.PublishAsync(bobsHost, Event(bobsRun, 2, RemoteEventKind.Completed, Sealed("his")));

        await database.ExecuteAsync(
            $"""
            UPDATE notices SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE owner_id = '{bob.UserId}';
            UPDATE events SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE owner_id = '{carol.UserId}';
            """);

        await Trimmer.TrimAsync();

        var bobs = await ReadAsync(bob);
        Assert.Empty(bobs.Notices);
        Assert.Equal(2, bobs.Events.Count);
        Assert.NotNull(bobs.Retention.TrimmedBefore);

        var carols = await ReadAsync(carol);
        Assert.Empty(carols.Events);
        Assert.NotNull(carols.Retention.TrimmedBefore);

        var alices = await ReadAsync(alice);
        Assert.Single(alices.Notices);
        Assert.Equal(2, alices.Events.Count);
        Assert.Null(alices.Retention.TrimmedBefore);
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM user_retention WHERE owner_id = '{alice.UserId}' AND trimmed_at IS NOT NULL"));
    }
}
