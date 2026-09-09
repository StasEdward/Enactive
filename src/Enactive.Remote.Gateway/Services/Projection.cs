namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>What the panel is shown. Read-only, capped, and free of anything secret.</summary>
public sealed record GatewaySnapshot(
    IReadOnlyList<HostView> Hosts,
    IReadOnlyList<TaskView> Tasks,
    IReadOnlyList<RunView> Runs,
    IReadOnlyList<ApprovalView> Approvals,
    IReadOnlyList<NoticeView> Notices,
    IReadOnlyList<EventView> Events,
    long Cursor,
    bool Delta,
    int UnreadNotices,
    RetentionView Retention);

/// <summary>
/// How long history is kept, and what has already gone.
///
/// <para><see cref="TrimmedBefore"/> is null until something has actually been deleted, and is the
/// cutoff of the run that deleted it after that. A window on its own would let the panel announce
/// that history had been trimmed on a gateway three days old - true of nothing, and read as a
/// warning about data that never existed.</para>
/// </summary>
public sealed record RetentionView(int Days, DateTimeOffset? TrimmedBefore);

public sealed record HostView(
    string Id, string Name, bool Revoked, bool Online, DateTimeOffset? LastSeenAt,
    IReadOnlyList<WorkspaceRef> Workspaces);

public sealed record TaskView(
    string Id, string HostId, string WorkspaceId, string Title, string Prompt, DateTimeOffset CreatedAt);

public sealed record RunView(
    string Id, string TaskId, string HostId, RemoteRunStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, string? Summary);

/// <summary>
/// A pending permission, complete. <see cref="Arguments"/> is the whole action and not a summary -
/// a person cannot approve what they were not shown - and <see cref="RemoteDecidable"/> is false
/// for a shell, which the panel must render as an explanation rather than a disabled button.
/// </summary>
public sealed record ApprovalView(
    string Id, string RunId, string Tool, string Arguments, string WorkingDirectory,
    string Reason, string ActionHash, bool RemoteDecidable, ApprovalStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record NoticeView(
    string Id, string RunId, string Title, string Detail, DateTimeOffset At, bool Read, long Ordinal);

public sealed record EventView(
    string Id, string RunId, long Sequence, RemoteEventKind Kind, string? Detail,
    DateTimeOffset At, long Ordinal);

/// <summary>
/// Builds the panel's view of the world.
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
/// showing Running on the panel for ever while the database says it finished hours ago. There are
/// several such paths already and there will be more. A bounded full read cannot go stale, and
/// bounded is all the claim needs - the cost stops growing with history, which is what actually
/// went wrong in the preview.</para>
///
/// <para>The preview returned every task, every run and every approval - with each approval's full
/// arguments, up to 24 000 characters - on a poll that ran every three seconds.</para>
/// </summary>
public sealed class Projection(Database database, Retention retention)
{
    private const int MaxEvents = 200;
    private const int MaxNotices = 100;
    private const int MaxRuns = 200;
    private const int MaxTasks = 200;

    /// <summary>A Host is offline after 45 seconds without a Sync, which it makes every 15.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The whole world, or only what has happened since <paramref name="since"/>.
    ///
    /// <para><b>Everything is read in one transaction, cursor included.</b> Without that, a row
    /// written between the last SELECT and the read of the cursor is passed over: the panel stores
    /// a cursor above it and never asks for anything that low again. One transaction makes the rows
    /// and the number that describes them the same instant.</para>
    ///
    /// <para>A delta that would exceed the cap is not truncated - truncating it would hand back a
    /// cursor covering rows the panel was never sent. It is answered with a full snapshot instead,
    /// and <see cref="GatewaySnapshot.Delta"/> says which of the two this is, so the panel appends
    /// or replaces on being told rather than on guessing from what it asked for.</para>
    /// </summary>
    public async Task<GatewaySnapshot> ReadAsync(long? since = null, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        var offlineBefore = DateTimeOffset.UtcNow - Projection.OfflineAfter;

        var workspaces = await connection.ReadAllAsync(transaction,
            "SELECT host_id, workspace_id, name FROM host_workspaces ORDER BY name",
            reader => (
                HostId: reader.GetString("host_id"),
                Workspace: new WorkspaceRef(reader.GetString("workspace_id"), reader.GetString("name"))));

        var hosts = await connection.ReadAllAsync(transaction,
            "SELECT id, name, revoked, last_seen_at FROM hosts ORDER BY created_at",
            reader =>
            {
                var id = reader.GetString("id");
                var revoked = reader.GetBoolean("revoked");
                var lastSeen = reader.UtcOrNull("last_seen_at");

                return new HostView(id, reader.GetString("name"), revoked,
                    !revoked && lastSeen > offlineBefore, lastSeen, []);
            });

        var withWorkspaces = hosts
            .Select(host => host with
            {
                Workspaces = workspaces.Where(w => w.HostId == host.Id).Select(w => w.Workspace).ToArray()
            })
            .ToArray();

        var tasks = await connection.ReadAllAsync(transaction,
            $"SELECT id, host_id, workspace_id, title, prompt, created_at FROM tasks ORDER BY created_at DESC LIMIT {MaxTasks}",
            reader => new TaskView(
                reader.GetString("id"), reader.GetString("host_id"), reader.GetString("workspace_id"),
                reader.GetString("title"), reader.GetString("prompt"), reader.Utc("created_at")));

        var runs = await connection.ReadAllAsync(transaction,
            $"SELECT id, task_id, host_id, status, created_at, ended_at, summary FROM runs ORDER BY created_at DESC LIMIT {MaxRuns}",
            reader => new RunView(
                reader.GetString("id"), reader.GetString("task_id"), reader.GetString("host_id"),
                reader.Enum<RemoteRunStatus>("status"), reader.Utc("created_at"),
                reader.UtcOrNull("ended_at"), reader.StringOrNull("summary")));

        // Only what is still being asked. A resolved approval's arguments are history, and history
        // that big does not belong in a poll.
        var approvals = await connection.ReadAllAsync(transaction,
            """
            SELECT id, run_id, tool, arguments, working_directory, reason, action_hash,
                   remote_decidable, status, created_at, expires_at
            FROM approvals
            WHERE status IN ('Pending', 'DecisionQueued')
            ORDER BY created_at
            """,
            reader => new ApprovalView(
                reader.GetString("id"), reader.GetString("run_id"), reader.GetString("tool"),
                reader.GetString("arguments"), reader.GetString("working_directory"),
                reader.GetString("reason"), reader.GetString("action_hash"),
                reader.GetBoolean("remote_decidable"), reader.Enum<ApprovalStatus>("status"),
                reader.Utc("created_at"), reader.Utc("expires_at")));

        // Read state is not part of the delta. Marking notices read updates rows the panel has
        // already been sent, and an update is exactly what an append-only stream cannot carry - so
        // the count comes back whole on every poll instead, which is one indexed lookup and is
        // correct even when a second browser is the one that read them.
        var unread = await connection.ReadOneAsync(transaction,
            "SELECT COUNT(*) AS n FROM notices WHERE is_read = 0",
            reader => reader.GetInt32("n"));

        // Ask for one more than the cap. Getting it back is how we learn the delta is too big to
        // answer honestly, without a second COUNT and without ever returning the extra row.
        List<EventView> newEvents =
            since is null ? [] : await ReadEventsAsync(connection, transaction, since.Value, MaxEvents + 1);
        List<NoticeView> newNotices =
            since is null ? [] : await ReadNoticesAsync(connection, transaction, since.Value, MaxNotices + 1);
        var delta = since is not null && newEvents.Count <= MaxEvents && newNotices.Count <= MaxNotices;

        if (!delta)
        {
            // Read newest-first so the LIMIT keeps the END of the history, then turned round: both
            // streams reach the panel oldest-first whichever way they were fetched, so appending a
            // delta to a full load is appending and not merging.
            newEvents = await ReadEventsAsync(connection, transaction, since: null, MaxEvents);
            newEvents.Reverse();
            newNotices = await ReadNoticesAsync(connection, transaction, since: null, MaxNotices);
            newNotices.Reverse();
        }

        // The counter, not MAX(ordinal): a number that has been allocated but not committed is not
        // visible here, so the cursor cannot run ahead of a row that is about to appear. It is also
        // the one value trimming cannot move backwards.
        var cursor = await connection.ReadOneAsync(transaction,
            "SELECT value FROM counters WHERE name = 'stream'", reader => reader.GetInt64("value"));

        var trimmedBefore = await connection.ReadOneAsync(transaction,
            "SELECT trimmed_before FROM retention_state WHERE id = 1",
            reader => reader.UtcOrNull("trimmed_before"));

        await transaction.CommitAsync(ct);

        return new GatewaySnapshot(
            withWorkspaces, tasks, runs, approvals, newNotices, newEvents,
            cursor, delta, unread, new RetentionView(retention.Days, trimmedBefore));
    }

    /// <summary>
    /// Newest <paramref name="limit"/> when <paramref name="since"/> is null, oldest-first above the
    /// cursor when it is not. The two orderings are deliberate: a first load wants the END of the
    /// history and a poll wants the BEGINNING of what it missed, and taking the newest rows of a
    /// delta would silently skip the middle of it.
    /// </summary>
    private static Task<List<EventView>> ReadEventsAsync(
        MySqlConnection connection, MySqlTransaction transaction, long? since, int limit)
        => connection.ReadAllAsync(transaction,
            since is null
                ? $"SELECT id, run_id, sequence, kind, detail, at, ordinal FROM events ORDER BY ordinal DESC LIMIT {limit}"
                : $"SELECT id, run_id, sequence, kind, detail, at, ordinal FROM events WHERE ordinal > @since ORDER BY ordinal LIMIT {limit}",
            reader => new EventView(
                reader.GetString("id"), reader.GetString("run_id"), reader.GetInt64("sequence"),
                reader.Enum<RemoteEventKind>("kind"), reader.StringOrNull("detail"),
                reader.Utc("at"), reader.GetInt64("ordinal")),
            ("@since", since));

    private static Task<List<NoticeView>> ReadNoticesAsync(
        MySqlConnection connection, MySqlTransaction transaction, long? since, int limit)
        => connection.ReadAllAsync(transaction,
            since is null
                ? $"SELECT id, run_id, title, detail, at, is_read, ordinal FROM notices ORDER BY ordinal DESC LIMIT {limit}"
                : $"SELECT id, run_id, title, detail, at, is_read, ordinal FROM notices WHERE ordinal > @since ORDER BY ordinal LIMIT {limit}",
            reader => new NoticeView(
                reader.GetString("id"), reader.GetString("run_id"), reader.GetString("title"),
                reader.GetString("detail"), reader.Utc("at"), reader.GetBoolean("is_read"),
                reader.GetInt64("ordinal")),
            ("@since", since));
}
