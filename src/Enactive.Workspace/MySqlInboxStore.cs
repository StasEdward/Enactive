namespace Enactive.Workspace;

using System.Globalization;
using MySqlConnector;
using Enactive.Core.Context;
using Enactive.Core.Inbox;

/// <summary>
/// MySQL-backed inbox, for a shared/server-backed deployment. Connection string comes from the host
/// (env ENACTIVE_MYSQL).
///
/// SCOPED BY WORKSPACE, and here that is not only about reading: one database serves every workspace
/// pointed at it, so an unscoped "mark all read" would clear other projects' inboxes as a side effect
/// of opening this one. Every statement carries the workspace id.
///
/// Best-effort like the other inbox stores: a failure drops the write rather than breaking a run.
/// </summary>
public sealed class MySqlInboxStore : IInboxStore
{
    private readonly string _connectionString;
    private readonly Guid _workspaceId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public MySqlInboxStore(string connectionString, WorkspaceInfo workspace)
    {
        _connectionString = connectionString;
        _workspaceId = workspace.Id;
    }

    public async Task AppendAsync(InboxItem item, CancellationToken ct)
    {
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO inbox
                  (id, workspace_id, kind, title, summary, run_id, status, at, schedule_id)
                VALUES
                  (@id, @workspace_id, @kind, @title, @summary, @run_id, @status, @at, @schedule_id)
                ON DUPLICATE KEY UPDATE
                  workspace_id=@workspace_id, kind=@kind, title=@title, summary=@summary,
                  run_id=@run_id, status=@status, at=@at, schedule_id=@schedule_id;
                """;
            command.Parameters.AddWithValue("@id", item.Id.ToString());
            command.Parameters.AddWithValue("@workspace_id", item.WorkspaceId.ToString());
            command.Parameters.AddWithValue("@kind", item.Kind);
            command.Parameters.AddWithValue("@title", item.Title);
            command.Parameters.AddWithValue("@summary", item.Summary);
            command.Parameters.AddWithValue("@run_id", item.RunId.ToString());
            command.Parameters.AddWithValue("@status", item.Status);
            command.Parameters.AddWithValue("@at", item.At.ToString("o"));
            command.Parameters.AddWithValue("@schedule_id",
                item.ScheduleId is { } schedule ? schedule.ToString() : (object)DBNull.Value);

            await command.ExecuteNonQueryAsync(ct);
        }
        catch { /* best-effort: the inbox is never load-bearing */ }
    }

    public async Task<IReadOnlyList<InboxItem>> LoadAllAsync(CancellationToken ct)
    {
        var results = new List<InboxItem>();
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, workspace_id, kind, title, summary, run_id, status, at, schedule_id
                FROM inbox
                WHERE workspace_id = @workspace_id
                ORDER BY at ASC;
                """;
            command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(new InboxItem(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    Guid.Parse(reader.GetString(5)),
                    reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    // Null, not Guid.Empty: a row written before the column existed did not come
                    // from "no schedule", it came from before anybody was recording which.
                    reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8))));
        }
        catch { /* best-effort */ }

        return results;
    }

    public Task MarkReadAsync(Guid id, CancellationToken ct)
        => UpdateStatusAsync(
            """
            UPDATE inbox SET status = 'read'
            WHERE workspace_id = @workspace_id AND id = @id;
            """, id, ct);

    public Task MarkAllReadAsync(CancellationToken ct)
        => UpdateStatusAsync(
            """
            UPDATE inbox SET status = 'read'
            WHERE workspace_id = @workspace_id;
            """, null, ct);

    private async Task UpdateStatusAsync(string sql, Guid? id, CancellationToken ct)
    {
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());
            if (id is { } value)
                command.Parameters.AddWithValue("@id", value.ToString());
            await command.ExecuteNonQueryAsync(ct);
        }
        catch { /* best-effort */ }
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS inbox (
                  id           CHAR(36) PRIMARY KEY,
                  workspace_id CHAR(36),
                  kind         VARCHAR(64),
                  title        TEXT,
                  summary      LONGTEXT,
                  run_id       CHAR(36),
                  status       VARCHAR(16),
                  at           VARCHAR(40),
                  schedule_id  CHAR(36) NULL,
                  INDEX ix_inbox_workspace (workspace_id, at)
                );
                """;
            await command.ExecuteNonQueryAsync(ct);

            // A database created before the column existed is upgraded in place. MySQL 8 has no
            // "ADD COLUMN IF NOT EXISTS" either, and a duplicate-column error here is the success
            // case: the column is already there.
            using (var upgrade = connection.CreateCommand())
            {
                upgrade.CommandText = "ALTER TABLE inbox ADD COLUMN schedule_id CHAR(36) NULL;";
                try { await upgrade.ExecuteNonQueryAsync(ct); }
                catch (MySqlException) { /* already has it */ }
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
