namespace Enactive.Workspace;

using System.Globalization;
using MySqlConnector;
using Enactive.Core.Context;
using Enactive.Core.Memory;

/// <summary>
/// MySQL-backed project memory, for a shared/server-backed deployment. Connection string comes from
/// the host (env ENACTIVE_MYSQL).
///
/// Unlike the run table, this one is SCOPED BY WORKSPACE: a MySQL database is shared by every
/// workspace pointed at it, and a memory entry already carries its WorkspaceId, so reads filter on
/// the workspace this store was built for. Otherwise one project's decisions would surface in
/// another project's timeline.
///
/// Best-effort like the other memory stores: a failure here drops the entry rather than breaking a run.
/// </summary>
public sealed class MySqlMemoryStore : IMemoryStore
{
    private readonly string _connectionString;
    private readonly Guid _workspaceId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public MySqlMemoryStore(string connectionString, WorkspaceInfo workspace)
    {
        _connectionString = connectionString;
        _workspaceId = workspace.Id;
    }

    public async Task AppendAsync(MemoryEntry entry, CancellationToken ct)
    {
        try
        {
            await EnsureSchemaAsync(ct);

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO memory
                  (id, workspace_id, kind, content, source_decision_id, at)
                VALUES
                  (@id, @workspace_id, @kind, @content, @source_decision_id, @at)
                ON DUPLICATE KEY UPDATE
                  workspace_id=@workspace_id, kind=@kind, content=@content,
                  source_decision_id=@source_decision_id, at=@at;
                """;
            command.Parameters.AddWithValue("@id", entry.Id.ToString());
            command.Parameters.AddWithValue("@workspace_id", entry.WorkspaceId.ToString());
            command.Parameters.AddWithValue("@kind", entry.Kind);
            command.Parameters.AddWithValue("@content", entry.Content);
            command.Parameters.AddWithValue("@source_decision_id", (object?)entry.SourceDecisionId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("@at", entry.At.ToString("o"));

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

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, workspace_id, kind, content, source_decision_id, at
                FROM memory
                WHERE workspace_id = @workspace_id
                ORDER BY at ASC;
                """;
            command.Parameters.AddWithValue("@workspace_id", _workspaceId.ToString());

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

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS memory (
                  id                 CHAR(36) PRIMARY KEY,
                  workspace_id       CHAR(36),
                  kind               VARCHAR(64),
                  content            LONGTEXT,
                  source_decision_id CHAR(36) NULL,
                  at                 VARCHAR(40),
                  INDEX ix_memory_workspace (workspace_id, at)
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
