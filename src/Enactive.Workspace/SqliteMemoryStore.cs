namespace Enactive.Workspace;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enactive.Core.Context;
using Enactive.Core.Memory;

/// <summary>
/// SQLite-backed project memory, in the same &lt;workspace&gt;/.enactive/enactive.db the run store uses,
/// so a workspace's history and its memory live in one file. One row per entry.
///
/// Best-effort, exactly like <see cref="JsonMemoryStore"/>: a memory failure must never break a run,
/// so an append that cannot be written is dropped and a load that fails returns nothing. Anything
/// worse than that - a misconfigured store - is caught loudly by
/// <see cref="MemoryStoreFactory"/> before a run ever starts.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _connectionString;
    private readonly string _legacyJsonPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteMemoryStore(WorkspaceInfo workspace)
    {
        var directory = Path.Combine(Path.GetFullPath(workspace.RootPath), ".enactive");
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "enactive.db")}";
        _legacyJsonPath = Path.Combine(directory, "memory.json");
    }

    public async Task AppendAsync(MemoryEntry entry, CancellationToken ct)
    {
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR REPLACE INTO memory
                  (id, workspace_id, kind, content, source_decision_id, at)
                VALUES
                  ($id, $workspace_id, $kind, $content, $source_decision_id, $at);
                """;
            command.Parameters.AddWithValue("$id", entry.Id.ToString());
            command.Parameters.AddWithValue("$workspace_id", entry.WorkspaceId.ToString());
            command.Parameters.AddWithValue("$kind", entry.Kind);
            command.Parameters.AddWithValue("$content", entry.Content);
            command.Parameters.AddWithValue("$source_decision_id", (object?)entry.SourceDecisionId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", entry.At.ToString("o"));

            await command.ExecuteNonQueryAsync(ct);
        }
        catch { /* best-effort: memory is never load-bearing */ }
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct)
    {
        var results = new List<MemoryEntry>();
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, workspace_id, kind, content, source_decision_id, at
                FROM memory
                ORDER BY at ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(new MemoryEntry(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        catch { /* best-effort */ }

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
                CREATE TABLE IF NOT EXISTS memory (
                  id                 TEXT PRIMARY KEY,
                  workspace_id       TEXT,
                  kind               TEXT,
                  content            TEXT,
                  source_decision_id TEXT,
                  at                 TEXT
                );
                """;
            await command.ExecuteNonQueryAsync(ct);

            await ImportLegacyJsonAsync(connection, ct);

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Carries a workspace's existing memory.json into the table, once, when the table is still
    /// empty. Without this, switching the default store from JSON to SQLite would silently empty
    /// every existing project's timeline - the file would still be sitting there, just no longer
    /// read. The JSON file is left alone, so the old store still works if you set ENACTIVE_STORE=json.
    ///
    /// Runs on the caller's already-open connection: it is inside the schema gate, so it must not go
    /// back through AppendAsync, which would wait on that same gate.
    /// </summary>
    private async Task ImportLegacyJsonAsync(SqliteConnection connection, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_legacyJsonPath))
                return;

            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM memory;";
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct) ?? 0L) > 0)
                    return;
            }

            var entries = JsonSerializer.Deserialize<List<MemoryEntry>>(
                await File.ReadAllTextAsync(_legacyJsonPath, ct), Json);
            if (entries is not { Count: > 0 })
                return;

            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var entry in entries)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText =
                    """
                    INSERT OR REPLACE INTO memory
                      (id, workspace_id, kind, content, source_decision_id, at)
                    VALUES
                      ($id, $workspace_id, $kind, $content, $source_decision_id, $at);
                    """;
                insert.Parameters.AddWithValue("$id", entry.Id.ToString());
                insert.Parameters.AddWithValue("$workspace_id", entry.WorkspaceId.ToString());
                insert.Parameters.AddWithValue("$kind", entry.Kind);
                insert.Parameters.AddWithValue("$content", entry.Content);
                insert.Parameters.AddWithValue("$source_decision_id", (object?)entry.SourceDecisionId?.ToString() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$at", entry.At.ToString("o"));
                await insert.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch { /* best-effort: a failed import must not stop the store from working */ }
    }
}
