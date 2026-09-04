namespace Enactive.Workspace;

using System.Globalization;
using System.Text.Json;
using MySqlConnector;
using Enactive.Core.History;

/// <summary>
/// MySQL-backed run store — same <see cref="IRunStore"/> contract as the SQLite and file stores, for a
/// shared/server-backed deployment. Connection string comes from the host (env ENACTIVE_MYSQL).
/// </summary>
public sealed class MySqlRunStore : IRunStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public MySqlRunStore(string connectionString) => _connectionString = connectionString;

    public async Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runs
              (run_id, task_id, title, model, started_at, finished_at, status, events_json, artifacts_json, decisions_json)
            VALUES
              (@run_id, @task_id, @title, @model, @started_at, @finished_at, @status, @events, @artifacts, @decisions)
            ON DUPLICATE KEY UPDATE
              task_id=@task_id, title=@title, model=@model, started_at=@started_at, finished_at=@finished_at,
              status=@status, events_json=@events, artifacts_json=@artifacts, decisions_json=@decisions;
            """;
        command.Parameters.AddWithValue("@run_id", record.RunId.ToString());
        command.Parameters.AddWithValue("@task_id", record.TaskId.ToString());
        command.Parameters.AddWithValue("@title", record.Title);
        command.Parameters.AddWithValue("@model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("@started_at", record.StartedAt.ToString("o"));
        command.Parameters.AddWithValue("@finished_at", record.FinishedAt.ToString("o"));
        command.Parameters.AddWithValue("@status", record.Status);
        command.Parameters.AddWithValue("@events", JsonSerializer.Serialize(record.Events, Json));
        command.Parameters.AddWithValue("@artifacts", JsonSerializer.Serialize(record.Artifacts, Json));
        command.Parameters.AddWithValue("@decisions", JsonSerializer.Serialize(record.Decisions, Json));

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
                   events_json, artifacts_json, decisions_json
            FROM runs
            ORDER BY started_at DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var events = JsonSerializer.Deserialize<List<RunEventRecord>>(reader.GetString(7), Json) ?? new();
            var artifacts = JsonSerializer.Deserialize<List<string>>(reader.GetString(8), Json) ?? new();
            var decisions = JsonSerializer.Deserialize<List<string>>(reader.GetString(9), Json) ?? new();

            results.Add(new RunRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(6),
                events, artifacts, decisions));
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
                  task_id        CHAR(36),
                  title          TEXT,
                  model          VARCHAR(255),
                  started_at     VARCHAR(40),
                  finished_at    VARCHAR(40),
                  status         VARCHAR(40),
                  events_json    LONGTEXT,
                  artifacts_json LONGTEXT,
                  decisions_json LONGTEXT
                );
                """;
            await command.ExecuteNonQueryAsync(ct);

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
