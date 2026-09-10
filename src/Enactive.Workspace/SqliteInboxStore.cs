namespace Enactive.Workspace;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enactive.Core.Context;
using Enactive.Core.Inbox;

/// <summary>
/// SQLite-backed inbox, in the same &lt;workspace&gt;/.enactive/enactive.db the run and memory stores
/// use. One row per item.
///
/// Marking read is what this buys over the file: the JSON store rewrote the entire inbox to flip one
/// item's status, so a large inbox paid for every click. Here it is an UPDATE.
///
/// Best-effort like every inbox store: a failure drops the write rather than breaking a run.
/// </summary>
public sealed class SqliteInboxStore : IInboxStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _connectionString;
    private readonly string _legacyJsonPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteInboxStore(WorkspaceInfo workspace)
    {
        var directory = Path.Combine(Path.GetFullPath(workspace.RootPath), ".enactive");
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "enactive.db")}";
        _legacyJsonPath = Path.Combine(directory, "inbox.json");
    }

    public async Task AppendAsync(InboxItem item, CancellationToken ct)
    {
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText = InsertSql;
            Bind(command, item);
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, workspace_id, kind, title, summary, run_id, status, at, schedule_id
                FROM inbox
                ORDER BY at ASC;
                """;

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
        => UpdateStatusAsync("UPDATE inbox SET status = 'read' WHERE id = $id;", id, ct);

    public Task MarkAllReadAsync(CancellationToken ct)
        => UpdateStatusAsync("UPDATE inbox SET status = 'read';", null, ct);

    private async Task UpdateStatusAsync(string sql, Guid? id, CancellationToken ct)
    {
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (id is { } value)
                command.Parameters.AddWithValue("$id", value.ToString());
            await command.ExecuteNonQueryAsync(ct);
        }
        catch { /* best-effort */ }
    }

    private const string InsertSql =
        """
        INSERT OR REPLACE INTO inbox
          (id, workspace_id, kind, title, summary, run_id, status, at, schedule_id)
        VALUES
          ($id, $workspace_id, $kind, $title, $summary, $run_id, $status, $at, $schedule_id);
        """;

    private static void Bind(SqliteCommand command, InboxItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$workspace_id", item.WorkspaceId.ToString());
        command.Parameters.AddWithValue("$kind", item.Kind);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$summary", item.Summary);
        command.Parameters.AddWithValue("$run_id", item.RunId.ToString());
        command.Parameters.AddWithValue("$status", item.Status);
        command.Parameters.AddWithValue("$at", item.At.ToString("o"));
        command.Parameters.AddWithValue("$schedule_id",
            item.ScheduleId is { } schedule ? schedule.ToString() : (object)DBNull.Value);
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS inbox (
                      id           TEXT PRIMARY KEY,
                      workspace_id TEXT,
                      kind         TEXT,
                      title        TEXT,
                      summary      TEXT,
                      run_id       TEXT,
                      status       TEXT,
                      at           TEXT,
                      schedule_id  TEXT
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }

            // A database created before the column existed is upgraded in place; SQLite has no
            // "ADD COLUMN IF NOT EXISTS", and a second run of this throws on a column that is
            // already there - which is the success case, not a failure. Same shape as the run store.
            using (var upgrade = connection.CreateCommand())
            {
                upgrade.CommandText = "ALTER TABLE inbox ADD COLUMN schedule_id TEXT;";
                try { await upgrade.ExecuteNonQueryAsync(ct); }
                catch (SqliteException) { /* already has it */ }
            }

            await ImportLegacyJsonAsync(connection, ct);

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Carries an existing inbox.json into the table once, while the table is still empty - otherwise
    /// changing the default store would quietly empty every workspace's inbox, file still on disk and
    /// no longer read. The file is left alone, so ENACTIVE_STORE=json keeps working.
    ///
    /// Runs on the caller's open connection, inside the schema gate: going back through AppendAsync
    /// would wait on that same gate.
    /// </summary>
    private async Task ImportLegacyJsonAsync(SqliteConnection connection, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_legacyJsonPath))
                return;

            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM inbox;";
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct) ?? 0L) > 0)
                    return;
            }

            var items = JsonSerializer.Deserialize<List<InboxItem>>(
                await File.ReadAllTextAsync(_legacyJsonPath, ct), Json);
            if (items is not { Count: > 0 })
                return;

            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var item in items)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = InsertSql;
                Bind(insert, item);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch { /* best-effort: a failed import must not stop the store from working */ }
    }
}
