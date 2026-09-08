namespace Enactive.Remote.Gateway.Storage;

using System.Globalization;
using System.Reflection;
using MySqlConnector;

/// <summary>
/// Brings a database up to the schema this build expects.
///
/// <para><b>MySQL cannot roll a migration back.</b> DDL commits implicitly, so "apply the file in a
/// transaction" - which is what <c>Docs/REMOTE_DESIGN.md</c> originally said, and what a reader
/// coming from PostgreSQL will assume - is not something this database offers. A statement that
/// fails half way through leaves everything before it applied and the version row unwritten.</para>
///
/// <para>So recovery is re-running, and re-running is only safe if every statement is written to be
/// harmless the second time. That is a rule about how migrations are WRITTEN, enforced by a test
/// that applies each one twice, not a promise the runner can keep on its own.</para>
///
/// <para>The version row is written only after the whole file succeeded, so a partial apply is
/// visible as "version not recorded" rather than being silently treated as done.</para>
/// </summary>
public static class Migrator
{
    /// <summary>
    /// Held while migrating. Two gateway processes starting together would otherwise both find the
    /// schema out of date and both try to create the same tables; the second one's failure would
    /// look like a corrupt database rather than a race.
    /// </summary>
    private const string LockName = "enactive_remote_migrate";

    private const int LockSeconds = 30;

    public static async Task<IReadOnlyList<int>> ApplyAsync(
        string connectionString, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        if (await ScalarAsync(connection, $"SELECT GET_LOCK('{LockName}', {LockSeconds})", ct) is not 1L)
        {
            throw new InvalidOperationException(
                $"Another process has held the '{LockName}' lock for more than {LockSeconds}s. "
                + "It is migrating, or it died holding the lock; nothing was changed here.");
        }

        try
        {
            await EnsureVersionTableAsync(connection, ct);
            var applied = await AppliedVersionsAsync(connection, ct);
            var newlyApplied = new List<int>();

            foreach (var (version, name, sql) in Migrations())
            {
                if (applied.Contains(version))
                {
                    continue;
                }

                await ExecuteAsync(connection, sql, ct);

                // Only now. A file that threw part way through leaves no row, so the next start
                // runs it again - which is exactly why every statement in it has to be repeatable.
                await ExecuteAsync(
                    connection,
                    $"INSERT INTO schema_version (version, applied_at) VALUES ({version}, UTC_TIMESTAMP(3))",
                    ct);

                newlyApplied.Add(version);
                _ = name;
            }

            return newlyApplied;
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT RELEASE_LOCK('{LockName}')", ct);
        }
    }

    /// <summary>The versions this build carries, whether or not any database has them.</summary>
    public static IReadOnlyList<int> KnownVersions() => Migrations().Select(m => m.Version).ToArray();

    /// <summary>
    /// The embedded <c>NNN_name.sql</c> files, in numeric order.
    ///
    /// <para>Ordered by the parsed number and not by the resource name, because resource names sort
    /// as text: with ten migrations, "010" would come before "9" and the schema would be built in
    /// the wrong order exactly once, on a fresh database, months from now.</para>
    /// </summary>
    private static IReadOnlyList<(int Version, string Name, string Sql)> Migrations()
    {
        var assembly = typeof(Migrator).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name =>
            {
                var file = name[(name.LastIndexOf("Migrations.", StringComparison.Ordinal) + "Migrations.".Length)..];
                var separator = file.IndexOf('_');

                if (separator < 0 || !int.TryParse(
                        file[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                {
                    throw new InvalidOperationException(
                        $"Migration '{file}' is not named NNN_description.sql, so its order is undefined.");
                }

                return (Version: version, Name: file, Sql: Read(assembly, name));
            })
            .OrderBy(m => m.Version)
            .ToArray();
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Migration resource '{resource}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Task EnsureVersionTableAsync(MySqlConnection connection, CancellationToken ct)
        => ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
              version    INT         NOT NULL PRIMARY KEY,
              applied_at DATETIME(3) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, ct);

    private static async Task<HashSet<int>> AppliedVersionsAsync(MySqlConnection connection, CancellationToken ct)
    {
        var versions = new HashSet<int>();
        await using var command = new MySqlCommand("SELECT version FROM schema_version", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(MySqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }
}
