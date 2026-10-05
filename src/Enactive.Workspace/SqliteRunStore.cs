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
              (run_id, task_id, title, model, started_at, finished_at, status, events_json, artifacts_json, decisions_json, settings_json, usage_json, spec_json)
            VALUES
              ($run_id, $task_id, $title, $model, $started_at, $finished_at, $status, $events, $artifacts, $decisions, $settings, $usage, $spec);
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
        command.Parameters.AddWithValue("$usage",
            record.Usage is null ? DBNull.Value : JsonSerializer.Serialize(record.Usage, Json));
        // Stored verbatim, not re-serialized: the snapshot's whole value is that it is byte-for-byte
        // what the run started from, so two runs can be compared by comparing their specifications.
        command.Parameters.AddWithValue("$spec", (object?)record.Spec ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runs WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The headers, without ever touching <c>events_json</c>. That column IS the run - every
    /// prompt, response and tool call - and the list needs none of it.
    /// </summary>
    public async Task<IReadOnlyList<RunSummary>> LoadSummariesAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        var results = new List<RunSummary>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, task_id, title, model, started_at, finished_at, status,
                   artifacts_json, decisions_json, settings_json, usage_json, spec_json
            FROM runs
            ORDER BY started_at DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new RunSummary(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(6),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(7), Json) ?? new(),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(8), Json) ?? new(),
                reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<RunSettings>(reader.GetString(9), Json),
                reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<RunUsage>(reader.GetString(10), Json),
                reader.IsDBNull(11) ? null : reader.GetString(11)));

        return results;
    }

    /// <summary>The columns a whole record is built from, in the order <see cref="ReadRecord"/>
    /// expects them. One string, so a query and its reader cannot drift apart.</summary>
    private const string RecordColumns =
        "run_id, task_id, title, model, started_at, finished_at, status, "
        + "events_json, artifacts_json, decisions_json, settings_json, usage_json, spec_json";

    /// <summary>One run, whole - what opening a row costs, paid only by the row that was opened.</summary>
    public async Task<RunRecord?> LoadAsync(Guid runId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RecordColumns} FROM runs WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        var results = new List<RunRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RecordColumns} FROM runs ORDER BY started_at DESC;";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRecord(reader));

        return results;
    }

    private static RunRecord ReadRecord(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(6),
            JsonSerializer.Deserialize<List<RunEventRecord>>(reader.GetString(7), Json) ?? new(),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(8), Json) ?? new(),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(9), Json) ?? new(),
            reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<RunSettings>(reader.GetString(10), Json),
            reader.IsDBNull(11) ? null : JsonSerializer.Deserialize<RunUsage>(reader.GetString(11), Json),
            reader.IsDBNull(12) ? null : reader.GetString(12));

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
                  settings_json  TEXT,
                  usage_json     TEXT,
                  spec_json      TEXT
                );
                """;
            await command.ExecuteNonQueryAsync(ct);

            // A database created before these columns existed is upgraded in place - see SqliteColumns.
            await SqliteColumns.AddIfMissingAsync(connection, "runs", "settings_json", "TEXT", ct);
            await SqliteColumns.AddIfMissingAsync(connection, "runs", "usage_json", "TEXT", ct);
            await SqliteColumns.AddIfMissingAsync(connection, "runs", "spec_json", "TEXT", ct);

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
