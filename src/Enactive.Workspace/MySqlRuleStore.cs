namespace Enactive.Workspace;

using System.Text.Json;
using MySqlConnector;
using Enactive.Core.Context;
using Enactive.Core.Rules;

/// <summary>
/// A workspace's rules in a shared MySQL database (Plan Phase 5.1). Scoped by workspace, as the memory table is
/// (<see cref="MySqlMemoryStore"/>): every workspace pointed at the database shares the table, and one project's rules
/// must not reach another's runs. The rule itself as JSON, as in <see cref="SqliteRuleStore"/>.
/// </summary>
public sealed class MySqlRuleStore : IRuleStore
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly string _connectionString;
    private readonly Guid _workspaceId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public MySqlRuleStore(string connectionString, WorkspaceInfo workspace)
    {
        _connectionString = connectionString;
        _workspaceId = workspace.Id;
    }

    public async Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_json FROM rules WHERE workspace_id = @workspace_id ORDER BY at ASC;";
        command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());
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
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO rules (id, workspace_id, at, entry_json) VALUES (@id, @workspace_id, @at, @entry)
            ON DUPLICATE KEY UPDATE at=@at, entry_json=@entry;
            """;
        command.Parameters.AddWithValue("@id", entry.Id.ToString());
        command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());
        command.Parameters.AddWithValue("@at", entry.At.ToString("o"));
        command.Parameters.AddWithValue("@entry", JsonSerializer.Serialize(entry, Json));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rules WHERE id = @id AND workspace_id = @workspace_id;";
        command.Parameters.AddWithValue("@id", id.ToString());
        command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());
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
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS rules (
                  id           CHAR(36) PRIMARY KEY,
                  workspace_id CHAR(36),
                  at           VARCHAR(40),
                  entry_json   LONGTEXT,
                  INDEX ix_rules_workspace (workspace_id, at)
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
