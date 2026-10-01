namespace Enactive.Remote.Gateway.Storage;

using System.Data;
using MySqlConnector;

/// <summary>
/// Opens connections and runs transactions on them, and nothing else.
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

    /// <summary>
    /// How many times one unit of work is run in all, the first time included.
    /// </summary>
    internal const int Attempts = 3;

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction at REPEATABLE READ and commits it. When the
    /// database picks it as the victim of a deadlock, the whole unit runs again, at most
    /// <see cref="Attempts"/> times in all.
    ///
    /// <para><b>Why a deadlock is retried.</b> The services take their locks in one order, which keeps
    /// their own paths from waiting on each other in a cycle; but InnoDB also deadlocks over gaps and
    /// index entries no order of rows covers, and it ends a deadlock by rolling one transaction back
    /// whole. Passed on, that was a 500 for a person whose request would have gone through a moment
    /// later, and for a computer an event its outbox retried and in the end parked.</para>
    ///
    /// <para><b>Why running it again is safe.</b> The victim was rolled back entirely, so the retry
    /// starts from nothing, and it is the same unit with the same ids: the same command id, event id
    /// or task id, whose idempotency checks are inside it. Nothing outside the database happens
    /// inside these transactions - a command is queued in a table, never sent, and the Host fetches it
    /// after the commit - so a retry cannot carry an action out twice.</para>
    ///
    /// <para><b>Why only a deadlock, and only three times.</b> Anything else - a duplicate key, a
    /// refusal, a lock wait that timed out - fails the same way again, and a retry would only make the
    /// caller wait longer for the same answer. A deadlock that recurs three times running is not bad
    /// luck but a cycle the lock order missed, and it has to be seen rather than retried for
    /// ever.</para>
    /// </summary>
    public async Task<T> InTransactionAsync<T>(
        Func<MySqlConnection, MySqlTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);

        for (var attempt = 1; ; attempt++)
        {
            await using (var transaction = await connection.BeginAsync(ct))
            {
                try
                {
                    var result = await work(connection, transaction);
                    await transaction.CommitAsync(ct);
                    return result;
                }
                catch (MySqlException error)
                    when (error.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < Attempts)
                {
                    // The server has already rolled the victim back; this ends the transaction on
                    // the connection too, so the next attempt begins a fresh one.
                    await transaction.RollbackAsync(CancellationToken.None);
                }
            }

            // Random, so the two transactions that collided do not start again in step and collide
            // the same way a second time.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 51)), ct);
        }
    }

    /// <inheritdoc cref="InTransactionAsync{T}"/>
    public Task InTransactionAsync(
        Func<MySqlConnection, MySqlTransaction, Task> work, CancellationToken ct)
        => InTransactionAsync<bool>(async (connection, transaction) =>
        {
            await work(connection, transaction);
            return true;
        }, ct);
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

    /// <summary>
    /// A transaction at REPEATABLE READ, which is MySQL's default and what <c>FOR UPDATE</c> is
    /// written against here.
    /// </summary>
    public static Task<MySqlTransaction> BeginAsync(this MySqlConnection connection, CancellationToken ct)
        => connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).AsTask();
}
