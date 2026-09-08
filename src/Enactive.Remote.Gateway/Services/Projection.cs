namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Storage;

/// <summary>What the panel is shown. Read-only, capped, and free of anything secret.</summary>
public sealed record GatewaySnapshot(
    IReadOnlyList<HostView> Hosts,
    IReadOnlyList<TaskView> Tasks,
    IReadOnlyList<RunView> Runs,
    IReadOnlyList<ApprovalView> Approvals,
    IReadOnlyList<NoticeView> Notices,
    IReadOnlyList<EventView> Events,
    long Cursor);

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

public sealed record NoticeView(string Id, string RunId, string Title, string Detail, DateTimeOffset At, bool Read);

public sealed record EventView(
    string Id, string RunId, long Sequence, RemoteEventKind Kind, string? Detail, DateTimeOffset At);

/// <summary>
/// Builds the panel's view of the world.
///
/// <para><b>Nothing here selects a token hash.</b> The row types in <c>Storage</c> carry one and
/// these view types do not, so the only way to leak it is to add a field on purpose - which is why
/// the two are separate types rather than one shared record with a couple of fields left out at
/// serialisation time.</para>
///
/// <para>Everything is capped. The preview returned every task, every run and every approval - with
/// each approval's full arguments, up to 24 000 characters - on a poll that ran every three
/// seconds, so the cost of showing the panel grew with the history for ever.</para>
/// </summary>
public sealed class Projection(Database database)
{
    private const int MaxEvents = 200;
    private const int MaxNotices = 100;
    private const int MaxRuns = 200;
    private const int MaxTasks = 200;

    /// <summary>A Host is offline after 45 seconds without a Sync, which it makes every 15.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(45);

    public async Task<GatewaySnapshot> ReadAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var offlineBefore = DateTimeOffset.UtcNow - OfflineAfter;

        var workspaces = await connection.ReadAllAsync(null,
            "SELECT host_id, workspace_id, name FROM host_workspaces ORDER BY name",
            reader => (
                HostId: reader.GetString("host_id"),
                Workspace: new WorkspaceRef(reader.GetString("workspace_id"), reader.GetString("name"))));

        var hosts = await connection.ReadAllAsync(null,
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

        var tasks = await connection.ReadAllAsync(null,
            $"SELECT id, host_id, workspace_id, title, prompt, created_at FROM tasks ORDER BY created_at DESC LIMIT {MaxTasks}",
            reader => new TaskView(
                reader.GetString("id"), reader.GetString("host_id"), reader.GetString("workspace_id"),
                reader.GetString("title"), reader.GetString("prompt"), reader.Utc("created_at")));

        var runs = await connection.ReadAllAsync(null,
            $"SELECT id, task_id, host_id, status, created_at, ended_at, summary FROM runs ORDER BY created_at DESC LIMIT {MaxRuns}",
            reader => new RunView(
                reader.GetString("id"), reader.GetString("task_id"), reader.GetString("host_id"),
                reader.Enum<RemoteRunStatus>("status"), reader.Utc("created_at"),
                reader.UtcOrNull("ended_at"), reader.StringOrNull("summary")));

        // Only what is still being asked. A resolved approval's arguments are history, and history
        // that big does not belong in a poll.
        var approvals = await connection.ReadAllAsync(null,
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

        var notices = await connection.ReadAllAsync(null,
            $"SELECT id, run_id, title, detail, at, is_read FROM notices ORDER BY at DESC LIMIT {MaxNotices}",
            reader => new NoticeView(
                reader.GetString("id"), reader.GetString("run_id"), reader.GetString("title"),
                reader.GetString("detail"), reader.Utc("at"), reader.GetBoolean("is_read")));

        var events = await connection.ReadAllAsync(null,
            $"SELECT id, run_id, sequence, kind, detail, at FROM events ORDER BY at DESC LIMIT {MaxEvents}",
            reader => new EventView(
                reader.GetString("id"), reader.GetString("run_id"), reader.GetInt64("sequence"),
                reader.Enum<RemoteEventKind>("kind"), reader.StringOrNull("detail"), reader.Utc("at")));

        events.Reverse();

        return new GatewaySnapshot(
            withWorkspaces, tasks, runs, approvals, notices, events,
            Cursor: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
