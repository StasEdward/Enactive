namespace Enactive.Remote.Gateway.Storage;

using System.Data;
using MySqlConnector;

/// <summary>
/// Opens connections, and nothing else.
///
/// <para>There is no repository layer over this on purpose. The SQL lives beside the decision it
/// serves, because in this gateway the interesting part of almost every operation IS the query -
/// which rows are locked, in what order, and what the database refuses. A layer that hid the SQL
/// would hide exactly the part worth reading.</para>
/// </summary>
public sealed class Database(string connectionString)
{
    public string ConnectionString { get; } = Normalise(connectionString);

    /// <summary>
    /// Settings the gateway insists on, whoever wrote the connection string.
    ///
    /// <para><c>GuidFormat=None</c> because MySqlConnector otherwise reads any <c>CHAR(36)</c>
    /// column back as a <see cref="Guid"/>, on the reasonable guess that a column of exactly that
    /// width holds one. Every id here is a STRING that we compare as text and never parse, so the
    /// guess is wrong, and it fails at the point of reading with a cast exception rather than
    /// anywhere near the cause. Found by a test on the first run of the command queue.</para>
    /// </summary>
    private static string Normalise(string connectionString)
        => new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.None
        }.ConnectionString;

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <summary>Parameter binding and reading, so the call sites stay the SQL and nothing else.</summary>
internal static class Sql
{
    public static MySqlCommand Command(
        this MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = new MySqlCommand(sql, connection, transaction);

        foreach (var (name, value) in parameters)
        {
            // DateTimeOffset has no MySQL type. Everything is stored as UTC in DATETIME(3), and
            // converting here rather than at each call site is what stops one query storing local
            // time because somebody passed DateTime.Now.
            command.Parameters.AddWithValue(name, value switch
            {
                DateTimeOffset at => at.UtcDateTime,
                Enum member => member.ToString(),
                bool flag => flag ? 1 : 0,
                _ => value
            });
        }

        return command;
    }

    public static async Task<int> ExecuteAsync(
        this MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.Command(transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ReadOneAsync<T>(
        this MySqlConnection connection, MySqlTransaction? transaction, string sql,
        Func<MySqlDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.Command(transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? map(reader) : default;
    }

    public static async Task<List<T>> ReadAllAsync<T>(
        this MySqlConnection connection, MySqlTransaction? transaction, string sql,
        Func<MySqlDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        var rows = new List<T>();
        await using var command = connection.Command(transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    public static async Task<bool> ExistsAsync(
        this MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.Command(transaction, sql, parameters);
        return await command.ExecuteScalarAsync() is not null;
    }

    /// <summary>
    /// A stored timestamp, read back as the UTC it was written as. MySQL hands back an unspecified
    /// <see cref="DateTime"/>; treating that as local time is how a run created at 09:00 UTC shows
    /// up three hours out on a panel and nobody can say which end was wrong.
    /// </summary>
    public static DateTimeOffset Utc(this MySqlDataReader reader, string column)
        => new(DateTime.SpecifyKind(reader.GetDateTime(column), DateTimeKind.Utc));

    public static DateTimeOffset? UtcOrNull(this MySqlDataReader reader, string column)
        => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.Utc(column);

    public static string? StringOrNull(this MySqlDataReader reader, string column)
        => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(column);

    /// <summary>
    /// An enum stored as its name.
    ///
    /// <para>A value the database holds and this build has never heard of is a bug or a rolled-back
    /// deployment, and either way it must not be guessed at: silently mapping it to the first member
    /// would turn an unknown status into "Queued" and restart a finished run.</para>
    /// </summary>
    public static T Enum<T>(this MySqlDataReader reader, string column) where T : struct, Enum
    {
        var stored = reader.GetString(column);

        return System.Enum.TryParse<T>(stored, ignoreCase: false, out var value)
            ? value
            : throw new InvalidOperationException(
                $"'{stored}' in column '{column}' is not a {typeof(T).Name} this build knows.");
    }

    public static T? EnumOrNull<T>(this MySqlDataReader reader, string column) where T : struct, Enum
        => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.Enum<T>(column);

    /// <summary>
    /// A transaction at REPEATABLE READ, which is MySQL's default and what <c>FOR UPDATE</c> is
    /// written against here.
    /// </summary>
    public static Task<MySqlTransaction> BeginAsync(this MySqlConnection connection, CancellationToken ct)
        => connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).AsTask();
}
