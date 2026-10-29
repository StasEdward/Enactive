namespace Enactive.Workspace;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Enactive.Core.Context;
using Enactive.Core.Rules;

/// <summary>
/// A workspace's rules in its &lt;workspace&gt;/.enactive/enactive.db (Plan Phase 5.1), beside its runs and memory, so
/// one store setting moves all of them. Scoped by the file, as the other SQLite stores are. One row per rule, the rule
/// itself as JSON: it is read whole and never queried by field, and a field added later needs no column.
/// </summary>
public sealed class SqliteRuleStore : IRuleStore
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteRuleStore(WorkspaceInfo workspace)
    {
        var directory = Path.Combine(Path.GetFullPath(workspace.RootPath), WorkspaceGuard.ReservedFolder);
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "enactive.db")}";
    }

    public async Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_json FROM rules ORDER BY at ASC;";
        var results = new List<RuleEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (JsonSerializer.Deserialize<RuleEntry>(reader.GetString(0), Json) is { } entry)
                results.Add(entry);
        return results;
    }

    public async Task SaveAsync(RuleEntry entry, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO rules (id, at, entry_json) VALUES ($id, $at, $entry);";
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$at", entry.At.ToString("o"));
        command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(entry, Json));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rules WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
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
                CREATE TABLE IF NOT EXISTS rules (
                  id         TEXT PRIMARY KEY,
                  at         TEXT,
                  entry_json TEXT
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
