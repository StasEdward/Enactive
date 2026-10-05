namespace Enactive.Workspace;

using Microsoft.Data.Sqlite;

/// <summary>
/// Upgrades a table made by an older build: adds a column it lacks, and leaves one it has alone.
///
/// <para>SQLite has no "ADD COLUMN IF NOT EXISTS", so the stores used to run the ALTER every time and catch
/// whatever it threw as "already has it". On every database but the oldest that throw is the normal path: each
/// start of the app raised "duplicate column name" into the debugger's output (2026-10-05). And the catch took ANY
/// SqliteException for success - a busy or read-only database left the column missing and the store marked ready,
/// its writes then failing one by one inside their best-effort catches. Asking the table first means the ALTER
/// runs only when it is needed, and a failure of it is a real failure.</para>
/// </summary>
internal static class SqliteColumns
{
    /// <summary>
    /// Adds <paramref name="column"/> to <paramref name="table"/> when it is not there. True when it was added
    /// now. The names are the caller's own constants, never input: an identifier cannot be a parameter.
    /// </summary>
    public static async Task<bool> AddIfMissingAsync(
        SqliteConnection connection, string table, string column, string definition, CancellationToken ct)
    {
        if (await HasAsync(connection, table, column, ct))
            return false;

        using var add = connection.CreateCommand();
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        try
        {
            await add.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqliteException)
        {
            // Another process on the same database - a second window on the workspace - added it between the
            // question and the ALTER. Anything else is not "already there", and is not swallowed.
            if (await HasAsync(connection, table, column, ct))
                return false;
            throw;
        }
    }

    private static async Task<bool> HasAsync(SqliteConnection connection, string table, string column, CancellationToken ct)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        probe.Parameters.AddWithValue("$table", table);
        probe.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await probe.ExecuteScalarAsync(ct) ?? 0L) > 0;
    }
}
