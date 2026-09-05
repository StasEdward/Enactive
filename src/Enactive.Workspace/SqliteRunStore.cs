namespace Enactive.Workspace;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enactive.Core.Context;
using Enactive.Core.History;

/// <summary>
/// SQLite-backed run store (one row per run, nested lists as JSON columns) under
/// &lt;workspace&gt;/.enactive/enactive.db. Same <see cref="IRunStore"/> contract as the file store, so
/// hosts swap one line. A MySQL variant would be the same shape with a different connection/provider.
/// </summary>
public sealed class SqliteRunStore : IRunStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteRunStore(WorkspaceInfo workspace)
    {
        var directory = Path.Combine(Path.GetFullPath(workspace.RootPath), ".enactive");
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "enactive.db")}";
    }

    public async Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO runs
              (run_id, task_id, title, model, started_at, finished_at, status, events_json, artifacts_json, decisions_json, settings_json)
            VALUES
              ($run_id, $task_id, $title, $model, $started_at, $finished_at, $status, $events, $artifacts, $decisions, $settings);
            """;
        command.Parameters.AddWithValue("$run_id", record.RunId.ToString());
        command.Parameters.AddWithValue("$task_id", record.TaskId.ToString());
        command.Parameters.AddWithValue("$title", record.Title);
        command.Parameters.AddWithValue("$model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$started_at", record.StartedAt.ToString("o"));
        command.Parameters.AddWithValue("$finished_at", record.FinishedAt.ToString("o"));
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$events", JsonSerializer.Serialize(record.Events, Json));
        command.Parameters.AddWithValue("$artifacts", JsonSerializer.Serialize(record.Artifacts, Json));
        command.Parameters.AddWithValue("$decisions", JsonSerializer.Serialize(record.Decisions, Json));
        command.Parameters.AddWithValue("$settings",
            record.Settings is null ? DBNull.Value : JsonSerializer.Serialize(record.Settings, Json));

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        var results = new List<RunRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, task_id, title, model, started_at, finished_at, status,
                   events_json, artifacts_json, decisions_json, settings_json
            FROM runs
            ORDER BY started_at DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var events = JsonSerializer.Deserialize<List<RunEventRecord>>(reader.GetString(7), Json) ?? new();
            var artifacts = JsonSerializer.Deserialize<List<string>>(reader.GetString(8), Json) ?? new();
            var decisions = JsonSerializer.Deserialize<List<string>>(reader.GetString(9), Json) ?? new();
            var settings = reader.IsDBNull(10)
                ? null
                : JsonSerializer.Deserialize<RunSettings>(reader.GetString(10), Json);

            results.Add(new RunRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(6),
                events, artifacts, decisions, settings));
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS runs (
                  run_id         TEXT PRIMARY KEY,
                  task_id        TEXT,
                  title          TEXT,
                  model          TEXT,
                  started_at     TEXT,
                  finished_at    TEXT,
                  status         TEXT,
                  events_json    TEXT,
                  artifacts_json TEXT,
                  decisions_json TEXT,
                  settings_json  TEXT
                );
                """;
            await command.ExecuteNonQueryAsync(ct);

            // A database created before the column existed is upgraded in place; SQLite has no
            // "ADD COLUMN IF NOT EXISTS", and a second run of this throws on a column that is
            // already there - which is the success case, not a failure.
            using var upgrade = connection.CreateCommand();
            upgrade.CommandText = "ALTER TABLE runs ADD COLUMN settings_json TEXT;";
            try { await upgrade.ExecuteNonQueryAsync(ct); }
            catch (SqliteException) { /* already has it */ }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
