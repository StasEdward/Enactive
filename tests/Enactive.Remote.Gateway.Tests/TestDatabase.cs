namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// A real MySQL database of this test class's own, created empty and dropped afterwards.
///
/// <para><b>Why a real server.</b> Everything worth testing about this schema is something only a
/// database enforces - a unique key that refuses an out-of-order event, a foreign key that refuses
/// an orphan, a binary collation that keeps two hashes apart. A fake would have to reimplement all
/// of it, and would then be testing the fake.</para>
///
/// <para><b>Why a database per class rather than a transaction per test.</b> Migrations are DDL,
/// and DDL commits implicitly in MySQL - there is no transaction to roll back. Isolation has to
/// come from the database name.</para>
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    private const string ConnectionVariable = "ENACTIVE_REMOTE_DB";

    /// <summary>
    /// Every test database is named for this prefix, which is also what the server grants rights on:
    /// <c>GRANT ALL ON `enactive\_test\_%`.*</c>. The account deliberately cannot create anything
    /// outside it, so a bug in this class cannot reach the gateway's own schema.
    /// </summary>
    public const string Prefix = "enactive_test_";

    public string Name { get; } = Prefix + Guid.NewGuid().ToString("N");

    public string ConnectionString { get; private set; } = "";

    private string _serverConnectionString = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);

        // Loud rather than skipped. A suite that quietly passes because it never ran is the exact
        // failure this codebase keeps writing tests against.
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{ConnectionVariable} is not set, so these tests have no database to run against. "
                + "Point it at a MySQL 8.0 server whose account may create databases named "
                + $"'{Prefix}...' - `docker compose up -d` provides one.");
        }

        var builder = new MySqlConnectionStringBuilder(configured);

        // The server without a database, for CREATE and DROP. Taking it apart with the builder
        // rather than by editing the string means a password containing a semicolon still works.
        _serverConnectionString = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        builder.Database = Name;

        // Through Database, so these tests connect on exactly the settings the gateway uses. A
        // suite that quietly ran with different driver options would be testing a different
        // program - which is how the CHAR(36)-read-back-as-Guid defect could have survived.
        ConnectionString = new Database(builder.ConnectionString).ConnectionString;

        await ExecuteOnServerAsync($"CREATE DATABASE `{Name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
        await Migrator.ApplyAsync(ConnectionString);
    }

    public async Task DisposeAsync() => await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS `{Name}`");

    /// <summary>An open connection to this test's own database.</summary>
    public async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <param name="parameters">
    /// For values a test supplies as PROSE. Ids a test generated are fine interpolated; a prompt
    /// with a quote or a backslash in it would fail as a SQL syntax error, which reads like a bug
    /// in whatever the test was actually about.
    /// </param>
    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public async Task<List<string>> StringsAsync(string sql)
    {
        var values = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>A single numeric answer - a COUNT, a sequence. Kept apart from
    /// <see cref="StringsAsync"/> so reading a number as text fails at the call site.</summary>
    public async Task<long> ScalarLongAsync(string sql) => Convert.ToInt64(await ScalarAsync(sql));

    public async Task<List<int>> IntsAsync(string sql)
    {
        var values = new List<int>();
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        return values;
    }

    /// <summary>Runs a statement, and returns the exception instead of throwing it.</summary>
    public async Task<MySqlException?> RefusedAsync(string sql)
    {
        try
        {
            await ExecuteAsync(sql);
            return null;
        }
        catch (MySqlException exception)
        {
            return exception;
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new MySqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
