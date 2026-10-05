namespace Enactive.Workspace;

using MySqlConnector;

/// <summary>
/// Upgrades a table made by an older build: adds a column or an index it lacks, and leaves one it has alone.
///
/// <para>The MySQL side of <see cref="SqliteColumns"/>, for the same reason. MySQL has no "ADD COLUMN IF NOT EXISTS"
/// and no "CREATE INDEX IF NOT EXISTS", so the stores ran each statement every time and caught any MySqlException
/// as "already there": a "Duplicate column name" thrown on every start, and a lost connection or a missing ALTER
/// privilege taken for success - the store marked ready over a table its INSERTs then failed on. The table is
/// asked first, through information_schema, in the database the connection is on.</para>
/// </summary>
internal static class MySqlColumns
{
    /// <summary>
    /// Adds <paramref name="column"/> to <paramref name="table"/> when it is not there. True when it was added
    /// now. The names are the caller's own constants, never input: an identifier cannot be a parameter.
    /// </summary>
    public static Task<bool> AddIfMissingAsync(
        MySqlConnection connection, string table, string column, string definition, CancellationToken ct)
        => CreateIfMissingAsync(connection,
            "SELECT COUNT(*) FROM information_schema.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @name;",
            table, column, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", ct);

    /// <summary>Creates index <paramref name="index"/> on <paramref name="table"/> when it is not there.</summary>
    public static Task<bool> IndexIfMissingAsync(
        MySqlConnection connection, string table, string index, string columns, CancellationToken ct)
        => CreateIfMissingAsync(connection,
            "SELECT COUNT(*) FROM information_schema.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND INDEX_NAME = @name;",
            table, index, $"CREATE INDEX {index} ON {table} ({columns});", ct);

    private static async Task<bool> CreateIfMissingAsync(
        MySqlConnection connection, string probeSql, string table, string name, string createSql, CancellationToken ct)
    {
        async Task<bool> Has()
        {
            using var probe = connection.CreateCommand();
            probe.CommandText = probeSql;
            probe.Parameters.AddWithValue("@table", table);
            probe.Parameters.AddWithValue("@name", name);
            return Convert.ToInt64(await probe.ExecuteScalarAsync(ct) ?? 0L) > 0;
        }

        if (await Has())
            return false;

        using var create = connection.CreateCommand();
        create.CommandText = createSql;
        try
        {
            await create.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (MySqlException)
        {
            // Another host sharing the database added it between the question and the statement - one server
            // serves every workspace pointed at it. Anything else is not "already there", and is not swallowed.
            if (await Has())
                return false;
            throw;
        }
    }
}
