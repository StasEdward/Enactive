namespace Enactive.Remote.Gateway.Services;

using System.Globalization;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>What one person's panel is shown. Read-only, capped, theirs, and free of anything secret.</summary>
/// <param name="Cursor">
/// <c>"{epoch}.{ordinal}"</c> on the person's own line: what the panel sends back as <c>since</c>.
/// </param>
public sealed record GatewaySnapshot(
    IReadOnlyList<HostView> Hosts,
    IReadOnlyList<TaskView> Tasks,
    IReadOnlyList<RunView> Runs,
    IReadOnlyList<ApprovalView> Approvals,
    IReadOnlyList<NoticeView> Notices,
    IReadOnlyList<EventView> Events,
    string Cursor,
    bool Delta,
    int UnreadNotices,
    RetentionView Retention);

/// <summary>
/// How long history is kept, and what has already gone.
///
/// <para><see cref="TrimmedBefore"/> is null until something of this person's has actually been
/// deleted, and is the cutoff of the pass that deleted it after that. A window on its own would let
/// the panel announce that history had been trimmed on an account three days old - true of nothing,
/// and read as a warning about data that never existed.</para>
/// </summary>
public sealed record RetentionView(int Days, DateTimeOffset? TrimmedBefore);

/// <summary>
/// A computer. The label is plaintext by design: it is what the person named the computer when they
/// registered it, and the panel needs it before it holds any key. <see cref="KeyEpoch"/> is the
/// computer's current key, which the panel needs to know whether it holds a grant for it.
/// </summary>
public sealed record HostView(
    string Id, string Label, bool Revoked, bool Online, DateTimeOffset? LastSeenAt, uint KeyEpoch,
    IReadOnlyList<WorkspaceView> Workspaces);

/// <summary>A workspace a computer published. Its name is sealed by that computer.</summary>
public sealed record WorkspaceView(string Id, string SealedName);

/// <summary>A task, as the browser sealed it.</summary>
public sealed record TaskView(
    string Id, string HostId, string WorkspaceId, string Sealed, DateTimeOffset CreatedAt);

/// <summary>
/// A run. <see cref="SealedSummary"/> is the terminal event's envelope, copied, and
/// <see cref="SummarySequence"/> that event's sequence - what the panel rebuilds its associated
/// data from, without which the copy could never be opened.
/// </summary>
public sealed record RunView(
    string Id, string TaskId, string HostId, RemoteRunStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, string? SealedSummary, long? SummarySequence);

/// <summary>
/// A pending permission, complete. <see cref="SealedAction"/> is the whole action and not a summary
/// - a person cannot approve what they were not shown - and <see cref="RemoteDecidable"/> is false
/// for a shell, which the panel must render as an explanation rather than a disabled button.
/// </summary>
public sealed record ApprovalView(
    string Id, string HostId, string RunId, string ToolCallId, string ActionHash, bool RemoteDecidable,
    string SealedAction, ApprovalStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>An event, sealed by the computer that reported it.</summary>
public sealed record EventView(
    string Id, string RunId, string HostId, long Sequence, RemoteEventKind Kind, string? SealedDetail,
    DateTimeOffset At, long Ordinal);

/// <summary>
/// A notice. <see cref="Kind"/> is the gateway's own word; the detail is the raising event's
/// envelope, copied, with that event's sequence and kind so the panel can rebuild what it was sealed
/// under. <see cref="HostId"/> is the run's computer, whose key opens it.
/// </summary>
public sealed record NoticeView(
    string Id, string RunId, string HostId, string Kind, string? SealedDetail, long? EventSequence,
    RemoteEventKind? EventKind, DateTimeOffset At, bool Read, long Ordinal);

/// <summary>
/// Builds one person's view of the world.
///
/// <para><b>Every read names the owner, before its LIMIT.</b> Each SELECT here has
/// <c>owner_id = @owner</c> in its WHERE and reads through a key that starts with the owner, so the
/// newest 200 are the newest 200 of this person's. Filtering the newest 200 of everybody's
/// afterwards would let a busy neighbour push a person's own runs off their panel, and an unfiltered
/// read would show them the neighbour's.</para>
///
/// <para><b>Nothing here selects a token hash.</b> The row types in <c>Storage</c> carry one and
/// these view types do not, so the only way to leak it is to add a field on purpose - which is why
/// the two are separate types rather than one shared record with a couple of fields left out at
/// serialisation time.</para>
///
/// <para><b>Two kinds of data, read two different ways.</b> Events and notices are append-only and
/// unbounded, so a poll asks only for what is newer than the cursor it holds. Hosts, tasks, runs
/// and approvals are mutable but BOUNDED - the newest 200, and only the approvals still being
/// asked - so every poll gets all of them.</para>
///
/// <para>A delta on the mutable sets was considered and refused. It needs a stamp on every row that
/// every update path remembers to bump, and a path that forgets does not fail: it leaves a run
/// showing Running on the panel for ever while the database says it finished hours ago. A bounded
/// full read cannot go stale, and bounded is all the claim needs - the cost stops growing with
/// history, which is what actually went wrong in the preview, whose poll returned every task, run
/// and approval every three seconds.</para>
/// </summary>
public sealed class Projection(Database database, Retention retention)
{
    private const int MaxEvents = 200;
    private const int MaxNotices = 100;
    private const int MaxRuns = 200;
    private const int MaxTasks = 200;

    // Longer than any cursor this gateway hands out ("{int}.{long}" is at most 30 characters). The
    // value arrives from a query string, and anything longer is refused before it is parsed rather
    // than handed to a number parser at whatever length somebody sent.
    private const int MaxCursorLength = 40;

    /// <summary>A Host is offline after 45 seconds without a Sync, which it makes every 15.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The person's whole world, or only what has happened on their line since <paramref name="since"/>.
    ///
    /// <para><b>Everything is read in one REPEATABLE READ transaction, cursor included.</b> Without
    /// that, a row written between the last SELECT and the read of the cursor is passed over: the
    /// panel stores a cursor above it and never asks for anything that low again. One transaction
    /// makes the rows, the unread count, the marker and the number that describes them one
    /// instant.</para>
    ///
    /// <para><b>The cursor needs no signature.</b> Every query is filtered by the caller's own
    /// account, so a forged cursor can only hide the forger's own events from the forger. What it
    /// carries besides the ordinal is the line's epoch, so that resetting a person's line - after a
    /// restore, say - invalidates every browser's cursor at once. A cursor that is malformed, of
    /// another epoch, or above the line's value names nothing on the current line, and gets the
    /// whole snapshot: answered as a delta, a cursor from the future would hide every row numbered
    /// up to it when they arrived.</para>
    ///
    /// <para>A delta that would exceed the cap is not truncated - truncating it would hand back a
    /// cursor covering rows the panel was never sent. It is answered with a full snapshot instead,
    /// and <see cref="GatewaySnapshot.Delta"/> says which of the two this is, so the panel appends
    /// or replaces on being told rather than on guessing from what it asked for.</para>
    /// </summary>
    public async Task<GatewaySnapshot> ReadAsync(UserAccess user, string? since, CancellationToken ct)
    {
        var owner = user.UserId;

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        // The counter, not MAX(ordinal): a number that has been allocated but not committed is not
        // visible here, so the cursor cannot run ahead of a row that is about to appear. It is also
        // the one value trimming cannot move backwards. Read first, because whether the cursor the
        // panel sent is one of this line's depends on it.
        //
        // Every account is provisioned with its line. Without one there is no cursor to hand out that
        // the next poll could be answered against, so a missing row is a broken account, said loudly.
        var line = await connection.ReadOneAsync(transaction,
            "SELECT value, epoch FROM user_streams WHERE owner_id = @owner",
            reader => ((long Value, int Epoch)?)(reader.GetInt64("value"), reader.GetInt32("epoch")),
            ("@owner", owner))
            ?? throw new InvalidOperationException("This account has no stream row, so it has no snapshot.");

        var after = OrdinalOnLine(since, line.Epoch, line.Value);
        var offlineBefore = DateTimeOffset.UtcNow - OfflineAfter;

        var workspaces = await connection.ReadAllAsync(transaction,
            """
            SELECT host_id, workspace_id, sealed_name FROM host_workspaces
            WHERE owner_id = @owner
            ORDER BY host_id, workspace_id
            """,
            reader => (
                HostId: reader.GetString("host_id"),
                Workspace: new WorkspaceView(reader.GetString("workspace_id"), reader.GetString("sealed_name"))),
            ("@owner", owner));

        // Not capped: how many computers a person may register is the account's limit to set, and a
        // panel that hid one of a person's computers would hide its requests with it.
        var hosts = await connection.ReadAllAsync(transaction,
            """
            SELECT id, label, revoked, last_seen_at, key_epoch FROM hosts
            WHERE owner_id = @owner
            ORDER BY created_at
            """,
            reader =>
            {
                var id = reader.GetString("id");
                var revoked = reader.GetBoolean("revoked");
                var lastSeen = reader.UtcOrNull("last_seen_at");

                return new HostView(id, reader.GetString("label"), revoked,
                    !revoked && lastSeen > offlineBefore, lastSeen, reader.GetUInt32("key_epoch"),
                    workspaces.Where(w => w.HostId == id).Select(w => w.Workspace).ToArray());
            },
            ("@owner", owner));

        var tasks = await connection.ReadAllAsync(transaction,
            $"""
            SELECT id, host_id, workspace_id, sealed, created_at FROM tasks
            WHERE owner_id = @owner
            ORDER BY created_at DESC
            LIMIT {MaxTasks}
            """,
            reader => new TaskView(
                reader.GetString("id"), reader.GetString("host_id"), reader.GetString("workspace_id"),
                reader.GetString("sealed"), reader.Utc("created_at")),
            ("@owner", owner));

        var runs = await connection.ReadAllAsync(transaction,
            $"""
            SELECT id, task_id, host_id, status, created_at, ended_at, sealed_summary, summary_sequence
            FROM runs
            WHERE owner_id = @owner
            ORDER BY created_at DESC
            LIMIT {MaxRuns}
            """,
            reader => new RunView(
                reader.GetString("id"), reader.GetString("task_id"), reader.GetString("host_id"),
                reader.Enum<RemoteRunStatus>("status"), reader.Utc("created_at"),
                reader.UtcOrNull("ended_at"), reader.StringOrNull("sealed_summary"),
                reader.IsDBNull(reader.GetOrdinal("summary_sequence")) ? null : reader.GetInt64("summary_sequence")),
            ("@owner", owner));

        // Only what is still being asked. A resolved approval's action is history, and history that
        // big does not belong in a poll. Not capped: a run waits on one request at a time, so the
        // requests still being asked are no more than the person's runs in flight.
        var approvals = await connection.ReadAllAsync(transaction,
            """
            SELECT id, host_id, run_id, tool_call_id, action_hash, remote_decidable, sealed_action,
                   status, created_at, expires_at
            FROM approvals
            WHERE owner_id = @owner AND status IN ('Pending', 'DecisionQueued')
            ORDER BY created_at
            """,
            reader => new ApprovalView(
                reader.GetString("id"), reader.GetString("host_id"), reader.GetString("run_id"),
                reader.GetString("tool_call_id"), reader.GetString("action_hash"),
                reader.GetBoolean("remote_decidable"), reader.GetString("sealed_action"),
                reader.Enum<ApprovalStatus>("status"), reader.Utc("created_at"), reader.Utc("expires_at")),
            ("@owner", owner));

        // Read state is not part of the delta. Marking notices read updates rows the panel has
        // already been sent, and an update is exactly what an append-only stream cannot carry - so
        // the count comes back whole on every poll instead, which is one indexed lookup and is
        // correct even when a second browser is the one that read them.
        var unread = await connection.ReadOneAsync(transaction,
            "SELECT COUNT(*) AS n FROM notices WHERE owner_id = @owner AND is_read = 0",
            reader => reader.GetInt32("n"), ("@owner", owner));

        // Ask for one more than the cap. Getting it back is how we learn the delta is too big to
        // answer honestly, without a second COUNT and without ever returning the extra row.
        List<EventView> newEvents =
            after is null ? [] : await ReadEventsAsync(connection, transaction, owner, after, MaxEvents + 1);
        List<NoticeView> newNotices =
            after is null ? [] : await ReadNoticesAsync(connection, transaction, owner, after, MaxNotices + 1);
        var delta = after is not null && newEvents.Count <= MaxEvents && newNotices.Count <= MaxNotices;

        if (!delta)
        {
            // Read newest-first so the LIMIT keeps the END of the history, then turned round: both
            // streams reach the panel oldest-first whichever way they were fetched, so appending a
            // delta to a full load is appending and not merging.
            newEvents = await ReadEventsAsync(connection, transaction, owner, after: null, MaxEvents);
            newEvents.Reverse();
            newNotices = await ReadNoticesAsync(connection, transaction, owner, after: null, MaxNotices);
            newNotices.Reverse();
        }

        var trimmedBefore = await connection.ReadOneAsync(transaction,
            "SELECT trimmed_before FROM user_retention WHERE owner_id = @owner",
            reader => reader.UtcOrNull("trimmed_before"), ("@owner", owner));

        await transaction.CommitAsync(ct);

        return new GatewaySnapshot(
            hosts, tasks, runs, approvals, newNotices, newEvents,
            string.Create(CultureInfo.InvariantCulture, $"{line.Epoch}.{line.Value}"),
            delta, unread, new RetentionView(retention.Days, trimmedBefore));
    }

    /// <summary>
    /// The ordinal <paramref name="cursor"/> names on the line it was read against, or null when it
    /// names nothing on it: absent, not "{epoch}.{ordinal}" in plain ASCII digits, of another epoch,
    /// or above the line's value. Null means a full snapshot, never a guess at what was meant.
    /// </summary>
    private static long? OrdinalOnLine(string? cursor, int epoch, long value)
        => TryParseCursor(cursor, out var cursorEpoch, out var ordinal) && cursorEpoch == epoch && ordinal <= value
            ? ordinal
            : null;

    /// <summary>
    /// <c>"{epoch}.{ordinal}"</c> in plain ASCII digits, taken apart. Whether it names anything on a
    /// person's line is a separate question, answered against that line. Shared with marking notices
    /// read, so the two never disagree about what a cursor is.
    /// </summary>
    internal static bool TryParseCursor(string? cursor, out int epoch, out long ordinal)
    {
        epoch = 0;
        ordinal = 0;

        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxCursorLength)
        {
            return false;
        }

        var dot = cursor.IndexOf('.');

        // NumberStyles.None: digits only. No sign, no spaces, no exponent and no group separators,
        // so a negative or decorated number is malformed rather than read as something else.
        return dot >= 0
            && int.TryParse(cursor.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out epoch)
            && long.TryParse(cursor.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal);
    }

    /// <summary>
    /// Newest <paramref name="limit"/> when <paramref name="after"/> is null, oldest-first above the
    /// cursor when it is not. The two orderings are deliberate: a first load wants the END of the
    /// history and a poll wants the BEGINNING of what it missed, and taking the newest rows of a
    /// delta would silently skip the middle of it. Both read the owner's ordinal key in order.
    /// </summary>
    private static Task<List<EventView>> ReadEventsAsync(
        MySqlConnection connection, MySqlTransaction transaction, string owner, long? after, int limit)
        => connection.ReadAllAsync(transaction,
            $"""
            SELECT id, run_id, host_id, sequence, kind, sealed_detail, at, ordinal FROM events
            WHERE owner_id = @owner {(after is null ? "" : "AND ordinal > @after")}
            ORDER BY ordinal {(after is null ? "DESC" : "")}
            LIMIT {limit}
            """,
            reader => new EventView(
                reader.GetString("id"), reader.GetString("run_id"), reader.GetString("host_id"),
                reader.GetInt64("sequence"), reader.Enum<RemoteEventKind>("kind"),
                reader.StringOrNull("sealed_detail"), reader.Utc("at"), reader.GetInt64("ordinal")),
            ("@owner", owner), ("@after", after));

    /// <summary>
    /// As <see cref="ReadEventsAsync"/>. A notice has no computer of its own; it is its run's, read
    /// through the run's owner key so the join cannot reach another person's run.
    /// </summary>
    private static Task<List<NoticeView>> ReadNoticesAsync(
        MySqlConnection connection, MySqlTransaction transaction, string owner, long? after, int limit)
        => connection.ReadAllAsync(transaction,
            $"""
            SELECT n.id, n.run_id, r.host_id, n.kind, n.sealed_detail, n.event_sequence, n.event_kind,
                   n.at, n.is_read, n.ordinal
            FROM notices n
            JOIN runs r ON r.owner_id = n.owner_id AND r.id = n.run_id
            WHERE n.owner_id = @owner {(after is null ? "" : "AND n.ordinal > @after")}
            ORDER BY n.ordinal {(after is null ? "DESC" : "")}
            LIMIT {limit}
            """,
            reader => new NoticeView(
                reader.GetString("id"), reader.GetString("run_id"), reader.GetString("host_id"),
                reader.GetString("kind"), reader.StringOrNull("sealed_detail"),
                reader.IsDBNull(reader.GetOrdinal("event_sequence")) ? null : reader.GetInt64("event_sequence"),
                reader.IsDBNull(reader.GetOrdinal("event_kind")) ? null : reader.Enum<RemoteEventKind>("event_kind"),
                reader.Utc("at"), reader.GetBoolean("is_read"), reader.GetInt64("ordinal")),
            ("@owner", owner), ("@after", after));
}
