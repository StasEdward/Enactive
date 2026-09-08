namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;
using Xunit;

/// <summary>
/// The panel's poll. Stage 6b of <c>Docs/REMOTE_DESIGN.md</c>.
///
/// <para>All of these are about one question: can the panel trust that "everything since 41" means
/// everything. A poll that loses a row loses it once, silently, for ever - the panel simply never
/// asks that low again - and for an ApprovalRequested notice that is a permission nobody is asked
/// for. So the tests here are mostly about what is NOT delivered by the cursor, and when.</para>
/// </summary>
public sealed class CursorTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private const string HostId = "3333333333333333333333333333cccc";

    private Database Db => new(database.ConnectionString);

    private HostService Host => new(Db);

    private Retention Trimmer => new(Db, days: 30);

    private Projection Panel => new(Db, Trimmer);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<string> RunningRunAsync()
    {
        await database.ExecuteAsync($"""
            INSERT IGNORE INTO hosts (id, name, token_hash, revoked, created_at)
            VALUES ('{HostId}', 'Host', SHA2('{HostId}', 256), 0, UTC_TIMESTAMP(3))
            """);

        var taskId = NewId();
        var runId = NewId();

        await database.ExecuteAsync($"""
            INSERT INTO tasks (id, host_id, workspace_id, title, prompt, created_at)
              VALUES ('{taskId}', '{HostId}', 'workspace-1', 'Test', 'Do it.', UTC_TIMESTAMP(3));
            INSERT INTO runs (id, task_id, host_id, status, created_at)
              VALUES ('{runId}', '{taskId}', '{HostId}', 'Queued', UTC_TIMESTAMP(3));
            """);

        await Host.PublishAsync(HostId, Event(runId, 1, RemoteEventKind.Running));
        return runId;
    }

    private static HostEvent Event(string runId, long sequence, RemoteEventKind kind, string? detail = null)
        => new(NewId(), runId, sequence, kind, detail, null, null);

    // ── the cursor ──────────────────────────────────────────────────────────

    /// <summary>
    /// The one this whole design exists for.
    ///
    /// <para>A publish is in flight - its ordinal is allocated and its row written, and it has not
    /// committed. The panel polls in that moment. If the cursor it is given covers that row, the
    /// row is lost: when the transaction commits the panel is already asking for something higher.
    /// </para>
    ///
    /// <para>Shown red by putting the cursor back to what it was first written as, the server's
    /// wall clock: the delta then asks for ordinals above a millisecond timestamp and the committed
    /// event never arrives.</para>
    /// </summary>
    [Fact]
    public async Task A_cursor_never_covers_a_row_that_has_not_committed()
    {
        var runId = await RunningRunAsync();

        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var inFlight = await connection.BeginAsync(default);

        // What PublishAsync does, held open: a number taken, a row written, nothing committed.
        var ordinal = await StreamCursor.NextAsync(connection, inFlight);
        await connection.ExecuteAsync(inFlight,
            """
            INSERT INTO events (id, host_id, run_id, sequence, kind, detail, at, ordinal)
            VALUES (@id, @host, @run, 2, 'Progress', 'in flight', UTC_TIMESTAMP(3), @ordinal)
            """,
            ("@id", NewId()), ("@host", HostId), ("@run", runId), ("@ordinal", ordinal));

        var duringFlight = await Panel.ReadAsync();
        Assert.True(duringFlight.Cursor < ordinal,
            $"The cursor was {duringFlight.Cursor} while ordinal {ordinal} was still uncommitted.");

        await inFlight.CommitAsync();

        var afterwards = await Panel.ReadAsync(duringFlight.Cursor);
        Assert.Contains(afterwards.Events, e => e.Detail == "in flight");
    }

    /// <summary>
    /// Events and notices are one stream stored in two tables, so one number line covers both.
    /// A second counter would let an event and the notice it raised share an ordinal, and a panel
    /// polling above it would then be one row behind on whichever table it read second.
    ///
    /// <para>Shown red by giving <c>notices</c> a counter of its own.</para>
    /// </summary>
    [Fact]
    public async Task An_event_and_the_notice_it_raised_do_not_share_a_number()
    {
        var runId = await RunningRunAsync();
        var before = await Panel.ReadAsync();

        // A terminal event writes both an event row and a notice, in one transaction.
        await Host.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.Completed, "done"));

        var after = await Panel.ReadAsync(before.Cursor);
        var ordinals = after.Events.Select(e => e.Ordinal).Concat(after.Notices.Select(n => n.Ordinal)).ToArray();

        Assert.Equal(2, ordinals.Length);
        Assert.Equal(ordinals.Length, ordinals.Distinct().Count());
        Assert.All(ordinals, ordinal => Assert.True(ordinal <= after.Cursor));
    }

    /// <summary>
    /// A poll that catches up returns nothing the next time. The cheap half, and it is here because
    /// a delta that re-sent the last row on every poll would still look correct to every other test
    /// in this file while costing exactly what the delta was written to stop costing.
    /// </summary>
    [Fact]
    public async Task A_second_poll_with_the_returned_cursor_carries_nothing()
    {
        var runId = await RunningRunAsync();
        await Host.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.Progress, "reading"));

        var first = await Panel.ReadAsync();
        var second = await Panel.ReadAsync(first.Cursor);

        Assert.True(second.Delta);
        Assert.Empty(second.Events);
        Assert.Empty(second.Notices);
    }

    /// <summary>
    /// A panel that has been closed for a while asks for more than a poll may carry. Truncating the
    /// answer is the tempting thing and the wrong one: the cursor that comes back with it would
    /// cover rows that were never sent, and the panel would never ask for them again.
    ///
    /// <para>So the reply is a full snapshot, and says so. Shown red by capping the delta with a
    /// LIMIT and returning it as a delta anyway - the newest event is then missing from a reply
    /// whose cursor claims to have passed it.</para>
    /// </summary>
    [Fact]
    public async Task A_delta_too_large_to_carry_comes_back_as_a_whole_snapshot()
    {
        var runId = await RunningRunAsync();
        var start = (await Panel.ReadAsync()).Cursor;

        await BackfillEventsAsync(runId, count: 250, detail: "bulk");

        var reply = await Panel.ReadAsync(start);

        Assert.False(reply.Delta);
        Assert.Contains(reply.Events, e => e.Sequence == 251);
        Assert.True(reply.Cursor >= reply.Events.Max(e => e.Ordinal));
    }

    /// <summary>
    /// Events written straight to the table, taking a block of ordinals in one go. This is a
    /// fixture and not the write path: <see cref="HostService"/> takes them one at a time under the
    /// lock, which is the property <see cref="A_cursor_never_covers_a_row_that_has_not_committed"/>
    /// is about. What matters here is only that the rows exist and the counter is not left behind
    /// them, because a counter behind a row would re-deliver it for ever.
    /// </summary>
    private async Task BackfillEventsAsync(string runId, int count, string detail)
    {
        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginAsync(default);

        await connection.ExecuteAsync(transaction,
            "UPDATE counters SET value = value + @count WHERE name = 'stream'", ("@count", count));

        var last = await connection.ReadOneAsync(transaction,
            "SELECT value FROM counters WHERE name = 'stream'", reader => reader.GetInt64("value"));

        var rows = Enumerable.Range(0, count).Select(i =>
            $"('{NewId()}', '{HostId}', '{runId}', {i + 2}, 'Progress', '{detail}', "
            + $"UTC_TIMESTAMP(3), {last - count + 1 + i})");

        await connection.ExecuteAsync(transaction,
            "INSERT INTO events (id, host_id, run_id, sequence, kind, detail, at, ordinal) VALUES "
            + string.Join(",", rows));

        await transaction.CommitAsync();
    }

    // ── retention ───────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing was old enough, so nothing is announced. A gateway three days old that told the
    /// panel history before last month had been trimmed would be describing data that never
    /// existed, and the notice would stop meaning anything on the day it did.
    ///
    /// <para>Shown red by stamping the cutoff on every pass instead of only on one that deleted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_pass_that_deletes_nothing_says_nothing_was_trimmed()
    {
        await RunningRunAsync();

        // Read before rather than asserting null: the tests in this class share one database and
        // its neighbours trim on purpose. What is being asserted is that THIS pass changed nothing,
        // not that nothing in the world has ever been trimmed.
        var before = (await Panel.ReadAsync()).Retention.TrimmedBefore;

        Assert.Equal(0, await Trimmer.TrimAsync());
        Assert.Equal(before, (await Panel.ReadAsync()).Retention.TrimmedBefore);
    }

    /// <summary>
    /// And when history IS discarded the panel is told, so a short list reads as a trimmed history
    /// rather than as a quiet fortnight.
    /// </summary>
    [Fact]
    public async Task History_past_the_window_is_deleted_and_the_panel_is_told()
    {
        var runId = await RunningRunAsync();
        await Host.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.Progress, "long ago"));

        await database.ExecuteAsync(
            $"UPDATE events SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE run_id = '{runId}'");

        Assert.Equal(2, await Trimmer.TrimAsync());

        var state = await Panel.ReadAsync();
        Assert.DoesNotContain(state.Events, e => e.RunId == runId);
        Assert.NotNull(state.Retention.TrimmedBefore);
        Assert.Equal(30, state.Retention.Days);
    }

    /// <summary>
    /// What makes deleting events safe at all.
    ///
    /// <para>Deduplication asks whether this event id is already stored, and a trimmed row answers
    /// no. The backstop is the run's <c>applied_sequence</c>, which is on the run row, is never
    /// trimmed, and refuses anything at or below the high-water mark - so the replay is dropped
    /// rather than applied a second time.</para>
    ///
    /// <para>Shown red by removing that sequence check: the trimmed event is then accepted again
    /// and the run's history grows a duplicate of something that happened a year ago. The check is
    /// tested elsewhere for its own sake; this is the test that stops it being deleted as redundant
    /// once trimming exists.</para>
    /// </summary>
    [Fact]
    public async Task A_trimmed_event_replayed_is_dropped_and_not_applied_twice()
    {
        var runId = await RunningRunAsync();
        var progress = Event(runId, 2, RemoteEventKind.Progress, "trimmed");
        await Host.PublishAsync(HostId, progress);

        await database.ExecuteAsync(
            $"UPDATE events SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE id = '{progress.EventId}'");
        Assert.Equal(1, await Trimmer.TrimAsync());

        var fault = await Assert.ThrowsAsync<GatewayFault>(() => Host.PublishAsync(HostId, progress));

        Assert.Equal(FaultCode.SequenceAlreadyApplied, fault.Code);
        Assert.Equal(FaultDisposition.Drop, RemoteFaults.DispositionOf(fault.Code));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM events WHERE id = '{progress.EventId}'"));
    }

    /// <summary>
    /// Read state is not in the stream. Marking notices read UPDATES rows the panel already has,
    /// and an append-only delta cannot carry an update - so the count comes back whole every time.
    ///
    /// <para>Shown red by counting the unread notices in the delta instead of in the table. The
    /// middle poll is what does it: it carries no notices at all, and an unread count taken from
    /// what it carries would say zero while a permission sits unanswered on the previous screen.
    /// A test that only checked the count after marking them read would pass either way, because
    /// both answers are zero - which is what the first draft of this test did.</para>
    /// </summary>
    [Fact]
    public async Task An_empty_delta_still_reports_the_notices_that_are_unread()
    {
        var runId = await RunningRunAsync();
        await Host.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.Completed, "done"));

        var first = await Panel.ReadAsync();
        Assert.True(first.UnreadNotices > 0);

        // Nothing has happened since. The delta is empty and the count must not be.
        var quiet = await Panel.ReadAsync(first.Cursor);
        Assert.True(quiet.Delta);
        Assert.Empty(quiet.Notices);
        Assert.Equal(first.UnreadNotices, quiet.UnreadNotices);

        await new OwnerService(Db).MarkNoticesReadAsync();

        var afterReading = await Panel.ReadAsync(quiet.Cursor);
        Assert.Empty(afterReading.Notices);
        Assert.Equal(0, afterReading.UnreadNotices);
    }
}
