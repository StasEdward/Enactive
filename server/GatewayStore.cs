namespace Enactive.Server;

using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Single-instance transactional snapshot. Mutations persist before becoming visible.
/// This deliberately small MVP store can later be replaced by normalized tables.</summary>
public sealed class GatewayStore
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public GatewayStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var folder = configuration["ENACTIVE_DATA"] ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(folder);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "gateway.db") }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS gateway (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL); INSERT OR IGNORE INTO gateway VALUES (1, $empty)";
        command.Parameters.AddWithValue("$empty", JsonSerializer.Serialize(new GatewayState(), Json));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public T Read<T>(Func<GatewayState, T> read) => Access(read, false);
    public T Change<T>(Func<GatewayState, T> change) => Access(change, true);

    private T Access<T>(Func<GatewayState, T> action, bool write)
    {
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT payload FROM gateway WHERE id=1";
            var state = JsonSerializer.Deserialize<GatewayState>((string)command.ExecuteScalar()!, Json)!;
            if (state.SchemaVersion != 1) throw new InvalidOperationException("Unsupported gateway schema.");
            var result = action(state);
            if (write)
            {
                command.CommandText = "UPDATE gateway SET payload=$payload WHERE id=1";
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state, Json));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return result;
        }
    }
}
