namespace RemoteHostHarness;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

/// <summary>
/// The browser tests' database: made empty for a run (the gateway migrates it as it starts), dumped so a test
/// can look for content in the clear, and dropped afterwards. On the server <c>ENACTIVE_E2E_MYSQL</c> names.
/// </summary>
internal static partial class DatabaseCommand
{
    private const string ServerVariable = "ENACTIVE_E2E_MYSQL";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var verb, var name])
        {
            Console.Error.WriteLine("Usage: db create|drop|dump <database>");
            return 2;
        }

        // Only a test's own database, as the gateway tests' TestDatabase names them: a slip here must not be
        // able to drop the gateway's real one on a developer's server.
        if (!TestName().IsMatch(name))
        {
            Console.Error.WriteLine($"'{name}' is not a test database: its name must start enactive_test_.");
            return 2;
        }

        var server = Environment.GetEnvironmentVariable(ServerVariable);
        if (string.IsNullOrWhiteSpace(server))
        {
            Console.Error.WriteLine($"Set {ServerVariable} to the MySQL server's connection string.");
            return 2;
        }

        switch (verb)
        {
            case "create":
                await ExecuteAsync(server, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
                return 0;
            case "drop":
                await ExecuteAsync(server, $"DROP DATABASE IF EXISTS `{name}`");
                return 0;
            case "dump":
                Console.Out.Write(await DumpAsync(server, name));
                return 0;
            default:
                Console.Error.WriteLine($"Not a database command: {verb}");
                return 2;
        }
    }

    private static async Task ExecuteAsync(string server, string sql)
    {
        await using var connection = new MySqlConnection(new MySqlConnectionStringBuilder(server) { Database = "" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Every row of every table, as <c>{table: [{column: text}]}</c>. Every column, not only the ones meant to
    /// hold content: what the test looks for is content where it was not meant to be. Bytes are read as Latin-1,
    /// so text written into a binary column is still found.
    /// </summary>
    private static async Task<string> DumpAsync(string server, string name)
    {
        await using var connection = new MySqlConnection(new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var listing = new MySqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @name ORDER BY table_name", connection))
        {
            listing.Parameters.AddWithValue("@name", name);
            await using var reader = await listing.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        var dump = new Dictionary<string, List<Dictionary<string, string?>>>();
        foreach (var table in tables)
        {
            var rows = new List<Dictionary<string, string?>>();
            await using var select = new MySqlCommand($"SELECT * FROM `{table}`", connection);
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, string?>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                    {
                        byte[] bytes => Encoding.Latin1.GetString(bytes),
                        DateTime at => at.ToString("O"),
                        var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)
                    };
                }
                rows.Add(row);
            }
            dump[table] = rows;
        }

        return JsonSerializer.Serialize(dump);
    }

    [GeneratedRegex(@"^enactive_test_[a-z0-9_]{1,40}\z")]
    private static partial Regex TestName();
}
