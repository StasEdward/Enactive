namespace Enactive.Workspace;

using System.Globalization;
using System.Text.Json;
using MySqlConnector;
using Enactive.Core.History;

/// <summary>
/// MySQL-backed run store — same <see cref="IRunStore"/> contract as the SQLite and file stores, for a
/// shared/server-backed deployment. Connection string comes from the host (env ENACTIVE_MYSQL).
///
/// Scoped by workspace, like the memory and inbox stores already were. Without it, every project
/// sharing one connection string shared one history: opening a record then resolved its RELATIVE
/// artifact paths against whatever workspace happened to be open, so a file could be viewed — or
/// deleted — in the belief that it belonged to the run on screen.
/// </summary>
public sealed class MySqlRunStore : IRunStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _connectionString;
    private readonly Guid _workspaceId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public MySqlRunStore(string connectionString, Guid workspaceId)
    {
        _connectionString = connectionString;
        _workspaceId = workspaceId;
    }

    public async Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runs
              (run_id, workspace_id, task_id, title, model, started_at, finished_at, status, events_json, artifacts_json, decisions_json, settings_json, usage_json, spec_json)
            VALUES
              (@run_id, @workspace_id, @task_id, @title, @model, @started_at, @finished_at, @status, @events, @artifacts, @decisions, @settings, @usage, @spec)
            ON DUPLICATE KEY UPDATE
              workspace_id=@workspace_id, task_id=@task_id, title=@title, model=@model,
              started_at=@started_at, finished_at=@finished_at,
              status=@status, events_json=@events, artifacts_json=@artifacts, decisions_json=@decisions,
              settings_json=@settings, usage_json=@usage, spec_json=@spec;
            """;
        command.Parameters.AddWithValue("@run_id", record.RunId.ToString());
        command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());
        command.Parameters.AddWithValue("@task_id", record.TaskId.ToString());
        command.Parameters.AddWithValue("@title", record.Title);
        command.Parameters.AddWithValue("@model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("@started_at", record.StartedAt.ToString("o"));
        command.Parameters.AddWithValue("@finished_at", record.FinishedAt.ToString("o"));
        command.Parameters.AddWithValue("@status", record.Status);
        // Verbatim, not re-serialized: the snapshot's value is that it is byte-for-byte what the run
        // started from, so two runs can be compared by comparing their specifications.
        command.Parameters.AddWithValue("@spec", (object?)record.Spec ?? DBNull.Value);
        command.Parameters.AddWithValue("@events", JsonSerializer.Serialize(record.Events, Json));
        command.Parameters.AddWithValue("@artifacts", JsonSerializer.Serialize(record.Artifacts, Json));
        command.Parameters.AddWithValue("@decisions", JsonSerializer.Serialize(record.Decisions, Json));
        command.Parameters.AddWithValue("@settings",
            record.Settings is null ? DBNull.Value : JsonSerializer.Serialize(record.Settings, Json));
        command.Parameters.AddWithValue("@usage",
            record.Usage is null ? DBNull.Value : JsonSerializer.Serialize(record.Usage, Json));

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Scoped to THIS workspace, like every other query here. One database can hold several
    /// workspaces' runs, and a delete that matched on run id alone would reach into another
    /// workspace's history from a window that is not showing it.
    /// </summary>
    public async Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runs WHERE run_id = @run_id AND workspace_id = @workspace_id;";
        command.Parameters.AddWithValue("@run_id", runId.ToString());
        command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        var results = new List<RunRecord>();

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, task_id, title, model, started_at, finished_at, status,
                   events_json, artifacts_json, decisions_json, settings_json, usage_json, spec_json
            FROM runs
            WHERE workspace_id = @workspace_id
            ORDER BY started_at DESC;
            """;
        command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var events = JsonSerializer.Deserialize<List<RunEventRecord>>(reader.GetString(7), Json) ?? new();
            var artifacts = JsonSerializer.Deserialize<List<string>>(reader.GetString(8), Json) ?? new();
            var decisions = JsonSerializer.Deserialize<List<string>>(reader.GetString(9), Json) ?? new();
            var settings = reader.IsDBNull(10)
                ? null
                : JsonSerializer.Deserialize<RunSettings>(reader.GetString(10), Json);
            var usage = reader.IsDBNull(11)
                ? null
                : JsonSerializer.Deserialize<RunUsage>(reader.GetString(11), Json);
            var spec = reader.IsDBNull(12) ? null : reader.GetString(12);

            results.Add(new RunRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(6),
                events, artifacts, decisions, settings, usage, spec));
        }

        return results;
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
                CREATE TABLE IF NOT EXISTS runs (
                  run_id         CHAR(36) PRIMARY KEY,
                  workspace_id   CHAR(36),
                  task_id        CHAR(36),
                  title          TEXT,
                  model          VARCHAR(255),
                  started_at     VARCHAR(40),
                  finished_at    VARCHAR(40),
                  status         VARCHAR(40),
                  events_json    LONGTEXT,
                  artifacts_json LONGTEXT,
                  decisions_json LONGTEXT,
                  settings_json  LONGTEXT,
                  usage_json     LONGTEXT,
                  spec_json      LONGTEXT
                );
                """;
            await command.ExecuteNonQueryAsync(ct);

            // A table created before the column existed is upgraded in place. MySQL has no
            // "ADD COLUMN IF NOT EXISTS" before 8.0.29 either way, and a duplicate column is the
            // success case here, not a failure.
            using var upgrade = connection.CreateCommand();
            upgrade.CommandText = "ALTER TABLE runs ADD COLUMN settings_json LONGTEXT;";
            try { await upgrade.ExecuteNonQueryAsync(ct); }
            catch (MySqlException) { /* already has it */ }

            using var upgradeUsage = connection.CreateCommand();
            upgradeUsage.CommandText = "ALTER TABLE runs ADD COLUMN usage_json LONGTEXT;";
            try { await upgradeUsage.ExecuteNonQueryAsync(ct); }
            catch (MySqlException) { /* already has it */ }

            using var upgradeSpec = connection.CreateCommand();
            upgradeSpec.CommandText = "ALTER TABLE runs ADD COLUMN spec_json LONGTEXT;";
            try { await upgradeSpec.ExecuteNonQueryAsync(ct); }
            catch (MySqlException) { /* already has it */ }

            // Rows written before this column existed keep workspace_id NULL and are therefore not
            // listed by any workspace. They are NOT backfilled to whichever folder happens to be open:
            // their artifact paths are relative, so guessing wrong would show one project's files
            // under another project's run. They are still in the table for anyone who wants to
            // reassign them with a deliberate UPDATE.
            using var upgradeWorkspace = connection.CreateCommand();
            upgradeWorkspace.CommandText = "ALTER TABLE runs ADD COLUMN workspace_id CHAR(36);";
            try { await upgradeWorkspace.ExecuteNonQueryAsync(ct); }
            catch (MySqlException) { /* already has it */ }

            using var index = connection.CreateCommand();
            index.CommandText = "CREATE INDEX ix_runs_workspace ON runs (workspace_id, started_at);";
            try { await index.ExecuteNonQueryAsync(ct); }
            catch (MySqlException) { /* already has it */ }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
