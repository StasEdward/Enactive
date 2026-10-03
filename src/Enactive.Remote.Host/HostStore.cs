namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts;
using Microsoft.Data.Sqlite;

/// <summary>What a run is doing, as far as this machine knows.</summary>
public enum LocalRunState
{
    /// <summary>Accepted and about to begin. A crash here is reported Interrupted.</summary>
    Starting,

    /// <summary>Reported as started. A crash here is reported Interrupted.</summary>
    Running,

    /// <summary>A terminal event has been queued. Nothing more is owed about it.</summary>
    Ended
}

/// <summary>One event still owed to the gateway.</summary>
public sealed record OutboxItem(string EventId, string RunId, long Sequence, HostEvent Event, int Attempts);

/// <summary>
/// The Host's durable memory: which commands it has taken responsibility for, which events it
/// still owes, and which runs were in flight.
///
/// <para><b>Where it lives.</b> <c>%APPDATA%\Enactive</c>, never inside a workspace. The workspace
/// is the thing tools write to; a queue that decides whether a task runs twice does not belong
/// somewhere a task can edit it.</para>
///
/// <para><b>What it is for.</b> Delivery is at-least-once in both directions, so the only thing
/// standing between "the gateway sent it twice" and "the work happened twice" is a record written
/// before the work starts. Every method here exists to make one of those records at the right
/// moment - which is usually earlier than feels natural.</para>
/// </summary>
public sealed class HostStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly System.Threading.Lock _gate = new();

    public HostStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString);

        _connection.Open();

        // WAL so a reader never blocks the writer, and FULL synchronous because the entire point of
        // this file is to be right after the power goes out. The cost is a fsync per commit, on a
        // queue that sees a handful of rows per run.
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;

            CREATE TABLE IF NOT EXISTS inbox (
              command_id      TEXT PRIMARY KEY,
              kind            TEXT NOT NULL,
              payload         TEXT NOT NULL,
              received_at     TEXT NOT NULL,
              acknowledged_at TEXT NULL,
              applied_at      TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS outbox (
              event_id   TEXT PRIMARY KEY,
              run_id     TEXT NOT NULL,
              sequence   INTEGER NOT NULL,
              payload    TEXT NOT NULL,
              created_at TEXT NOT NULL,
              attempts   INTEGER NOT NULL DEFAULT 0,
              last_fault TEXT NULL,
              parked     INTEGER NOT NULL DEFAULT 0,
              UNIQUE (run_id, sequence)
            );

            CREATE TABLE IF NOT EXISTS runs (
              run_id        TEXT PRIMARY KEY,
              command_id    TEXT NOT NULL,
              state         TEXT NOT NULL,
              next_sequence INTEGER NOT NULL DEFAULT 1,
              started_at    TEXT NOT NULL,
              ended_at      TEXT NULL
            );

            -- The computer's keys and whom it trusts, kept by HostKeyStore. In this file rather than
            -- one of their own so there is one place that says what this machine knows about remote
            -- access, and one lock in front of it. Every secret below is DPAPI-protected text.
            CREATE TABLE IF NOT EXISTS host_keys (
              epoch  INTEGER PRIMARY KEY,
              secret TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS host_signing (
              id          INTEGER PRIMARY KEY CHECK (id = 1),
              private_key TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS trusted_devices (
              device_id  TEXT PRIMARY KEY,
              public_key BLOB NOT NULL,
              label      TEXT NOT NULL,
              added_by   TEXT NOT NULL,
              added_at   TEXT NOT NULL,
              revoked_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS pending_invites (
              id         TEXT PRIMARY KEY,
              secret     TEXT NOT NULL,
              expires_at TEXT NOT NULL
            );

            -- device_id is its own column, not only part of the id, so revoking a device can drop
            -- exactly its queued grants without matching on the text of an id.
            CREATE TABLE IF NOT EXISTS pending_grants (
              id         TEXT PRIMARY KEY,
              device_id  TEXT NOT NULL,
              json       TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            -- Devices removed on this computer that the gateway has not yet been told of. Written with
            -- the removal itself, so a removal made offline, or on a connection that dropped, is passed
            -- on at the next connection instead of being lost.
            CREATE TABLE IF NOT EXISTS owed_revocations (
              device_id TEXT PRIMARY KEY,
              since     TEXT NOT NULL
            );
            """);
    }

    /// <summary>
    /// The connection, for the key store, while this store's lock is held.
    ///
    /// <para>The key store keeps its tables in this file, and a second connection to it would be a
    /// second writer that this lock knows nothing about. Handing out the connection only inside the
    /// lock keeps every statement against remote.db behind the same gate.</para>
    /// </summary>
    internal T Locked<T>(Func<SqliteConnection, T> work)
    {
        using var guard = _gate.EnterScope();
        return work(_connection);
    }

    // ── commands coming in ──────────────────────────────────────────────────

    /// <summary>
    /// Writes a command down, and says whether it is new.
    ///
    /// <para>This has to happen BEFORE the gateway is acknowledged. Acknowledging first and then
    /// crashing loses the command permanently: the gateway stops re-delivering an accepted command,
    /// and this machine has no record that it exists. That ordering is the whole reason
    /// "acknowledged" is a separate word from "delivered".</para>
    /// </summary>
    public bool Accept(HostCommand command)
    {
        using var guard = _gate.EnterScope();
        using var statement = _connection.CreateCommand();
        statement.CommandText = """
            INSERT INTO inbox (command_id, kind, payload, received_at)
            VALUES ($id, $kind, $payload, $now)
            ON CONFLICT (command_id) DO NOTHING
            """;
        Bind(statement, "$id", command.Id);
        Bind(statement, "$kind", command.Kind.ToString());
        Bind(statement, "$payload", command.Payload);
        Bind(statement, "$now", Now());

        return statement.ExecuteNonQuery() == 1;
    }

    public void MarkAcknowledged(string commandId)
        => Execute("UPDATE inbox SET acknowledged_at = $now WHERE command_id = $id",
            ("$now", Now()), ("$id", commandId));

    /// <summary>Whether this command has already been carried out, however the process ended.</summary>
    public bool WasApplied(string commandId)
        => Scalar("SELECT applied_at FROM inbox WHERE command_id = $id", ("$id", commandId)) is not null;

    /// <summary>
    /// Records that a command has been carried out - or refused for good, which is as final an answer.
    /// Written only once it has been, never when it is handed over: the inbox is what brings back a
    /// command whose carrying out a crash or a dropped connection cut short, and it can bring back only
    /// what is not marked. Marking a command already marked changes nothing, so the first time stands.
    /// </summary>
    public void MarkApplied(string commandId)
    {
        using var guard = _gate.EnterScope();
        MarkApplied(_connection, null, commandId);
    }

    /// <summary>
    /// The same, inside a transaction of the caller's: the key store marks a removal carried out in the
    /// step that makes it (<see cref="HostKeyStore.RevokeAndRotate"/>). The caller holds this store's lock.
    /// </summary>
    internal static void MarkApplied(SqliteConnection connection, SqliteTransaction? transaction, string commandId)
    {
        using var statement = connection.CreateCommand();
        statement.Transaction = transaction;
        statement.CommandText = "UPDATE inbox SET applied_at = $now WHERE command_id = $id AND applied_at IS NULL";
        Bind(statement, "$now", Now());
        Bind(statement, "$id", commandId);
        statement.ExecuteNonQuery();
    }

    /// <summary>
    /// Every command written down and not yet carried out, in the order it was received, but for one
    /// older than a command of its kind may wait.
    ///
    /// <para>This is what makes receiving a command and carrying it out two things a crash can come
    /// between without losing it. The gateway hands a command over until it is acknowledged, and an
    /// acknowledged one never again - so a command that was acknowledged and then not carried out, because
    /// the process ended, the connection dropped or its batch failed after it, was gone for good when only
    /// a redelivery could bring it back. A removal of a device lost so was refused by the gateway and
    /// still trusted here, and its key never replaced.</para>
    ///
    /// <para>How old is measured from when this computer received it, by the lifetime of its kind
    /// (<see cref="RemoteProtocol.LifetimeOf"/>), since the inbox does not keep the gateway's own expiry:
    /// the gateway made the command before it was received, so this errs on keeping one a little longer.
    /// What is kept a little longer is still not believed - the sealer refuses anything sealed longer ago
    /// than its kind may wait.</para>
    ///
    /// <para>The command is as it was written down. Its routing fields the inbox does not keep - nothing
    /// on this computer reads them, the sealer opens every command against this computer's own id - so
    /// the host id is empty, and the time it was made and the time it expires are counted from receipt.</para>
    /// </summary>
    public IReadOnlyList<HostCommand> Unapplied(DateTimeOffset now)
    {
        var commands = new List<HostCommand>();

        using var guard = _gate.EnterScope();
        using var statement = _connection.CreateCommand();

        // Nothing received longer ago than the longest lifetime there is can still be owed: kept out
        // here, so the rows a long-running computer piles up are not read on every turn.
        statement.CommandText = """
            SELECT command_id, kind, payload, received_at FROM inbox
            WHERE applied_at IS NULL AND received_at > $oldest
            ORDER BY received_at, rowid
            """;
        Bind(statement, "$oldest", (now - RemoteProtocol.DeviceCommandLifetime).ToUniversalTime().ToString("O"));

        using var reader = statement.ExecuteReader();
        while (reader.Read())
        {
            // A kind this build does not know was written by another build of it. Nothing here could
            // carry it out, so it is not handed on.
            if (!Enum.TryParse<CommandKind>(reader.GetString(1), out var kind))
            {
                continue;
            }

            var received = DateTimeOffset.Parse(
                reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
            var expires = received + RemoteProtocol.LifetimeOf(kind);

            if (expires <= now)
            {
                continue;
            }

            commands.Add(new HostCommand(
                reader.GetString(0), string.Empty, kind, reader.GetString(2),
                CommandStatus.AcceptedByHost, received, expires));
        }

        return commands;
    }

    /// <summary>
    /// Claims a start command and opens the run, in one transaction.
    ///
    /// <para><b><c>applied_at</c> is written before the run begins, not after.</b> A crash between
    /// this and the first event therefore leaves a record saying "this may have run", and recovery
    /// reports <see cref="RemoteEventKind.Interrupted"/> rather than starting it a second time. We
    /// do not know what happened, so we say we do not know - which is the honest answer and the
    /// only safe one, because the alternative is running a task twice.</para>
    ///
    /// <para>Returns false when the command was already applied: that is a redelivery, and the
    /// correct response is to do nothing at all.</para>
    /// </summary>
    public bool BeginRun(string commandId, string runId)
    {
        using var guard = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();

        var claimed = Execute(transaction,
            "UPDATE inbox SET applied_at = $now WHERE command_id = $id AND applied_at IS NULL",
            ("$now", Now()), ("$id", commandId));

        if (claimed == 0)
        {
            transaction.Rollback();
            return false;
        }

        Execute(transaction,
            """
            INSERT INTO runs (run_id, command_id, state, next_sequence, started_at)
            VALUES ($run, $command, $state, 1, $now)
            """,
            ("$run", runId), ("$command", commandId),
            ("$state", LocalRunState.Starting.ToString()), ("$now", Now()));

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Whether an ending has already been queued for this run.
    ///
    /// <para>A run has exactly one ending, and more than one place can reach the decision to write
    /// it - the stream finishing, a cancellation, an unhandled failure. A second would be refused
    /// by the gateway as an event about a run that has ended, which is the Host arguing with the
    /// far end about something it could have known here.</para>
    /// </summary>
    public bool HasEnded(string runId)
        => Scalar("SELECT 1 FROM runs WHERE run_id = $run AND state = $ended",
            ("$run", runId), ("$ended", LocalRunState.Ended.ToString())) is not null;

    public void MarkRunState(string runId, LocalRunState state)
        => Execute(
            "UPDATE runs SET state = $state, ended_at = CASE WHEN $ended = 1 THEN $now ELSE ended_at END "
            + "WHERE run_id = $run",
            ("$state", state.ToString()), ("$ended", state == LocalRunState.Ended ? 1 : 0),
            ("$now", Now()), ("$run", runId));

    /// <summary>
    /// Runs that were in flight when the process stopped: opened, and never finished.
    ///
    /// <para>This is where <see cref="RemoteEventKind.Interrupted"/> comes from. Nothing in the
    /// preview's protocol ever said who emitted it; it is emitted here, on the next start, for
    /// every run whose ending nobody wrote down.</para>
    /// </summary>
    public IReadOnlyList<string> RunsLeftInFlight()
    {
        var runs = new List<string>();

        using var guard = _gate.EnterScope();
        using var statement = _connection.CreateCommand();
        statement.CommandText = "SELECT run_id FROM runs WHERE state <> $ended ORDER BY started_at";
        Bind(statement, "$ended", LocalRunState.Ended.ToString());

        using var reader = statement.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(reader.GetString(0));
        }

        return runs;
    }

    // ── events going out ────────────────────────────────────────────────────

    /// <summary>
    /// Queues an event and gives it this run's next sequence number, in one transaction.
    ///
    /// <para>The number is allocated here rather than by the caller so it cannot be skipped, reused
    /// or handed out twice by two threads - the gateway refuses anything not ahead of what it has
    /// applied, and a duplicated number would mean an event that can never be delivered.</para>
    ///
    /// <para><paramref name="sealedDetail"/> is handed the sequence and returns the sealed sentence,
    /// rather than being a sealed string already: the detail is sealed under its event's sequence
    /// number, and that number exists only inside this transaction. A caller that guessed it would
    /// seal a sentence no browser could open whenever another thread queued first.</para>
    /// </summary>
    public HostEvent Enqueue(
        string runId, RemoteEventKind kind, Func<long, string>? sealedDetail = null,
        ApprovalRequest? approval = null, ApprovalResolution? resolution = null)
    {
        using var guard = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();

        var sequence = Convert.ToInt64(
            Scalar(transaction, "SELECT next_sequence FROM runs WHERE run_id = $run", ("$run", runId))
            ?? throw new InvalidOperationException($"Run {runId} was never opened here."));

        Execute(transaction, "UPDATE runs SET next_sequence = $next WHERE run_id = $run",
            ("$next", sequence + 1), ("$run", runId));

        var published = new HostEvent(
            Guid.NewGuid().ToString("N"), runId, sequence, kind, sealedDetail?.Invoke(sequence), approval, resolution);

        Execute(transaction,
            """
            INSERT INTO outbox (event_id, run_id, sequence, payload, created_at)
            VALUES ($id, $run, $sequence, $payload, $now)
            """,
            ("$id", published.EventId), ("$run", runId), ("$sequence", sequence),
            ("$payload", RemoteJson.Serialize(published)), ("$now", Now()));

        if (RunLifecycle.IsTerminal(kind))
        {
            Execute(transaction,
                "UPDATE runs SET state = $state, ended_at = $now WHERE run_id = $run",
                ("$state", LocalRunState.Ended.ToString()), ("$now", Now()), ("$run", runId));
        }

        transaction.Commit();
        return published;
    }

    /// <summary>
    /// The next event owed for each run - ONE per run, its lowest unsent sequence.
    ///
    /// <para>Strict order per run is what the gateway's rule requires, and holding only one in
    /// flight is how it is kept without any assumption about how the network reorders things. Runs
    /// do not block each other: a wedged run is one run, not the whole queue.</para>
    /// </summary>
    public IReadOnlyList<OutboxItem> NextOwed()
    {
        var items = new List<OutboxItem>();

        using var guard = _gate.EnterScope();
        using var statement = _connection.CreateCommand();
        statement.CommandText = """
            SELECT o.event_id, o.run_id, o.sequence, o.payload, o.attempts
            FROM outbox o
            WHERE o.parked = 0
              AND o.sequence = (SELECT MIN(i.sequence) FROM outbox i
                                WHERE i.run_id = o.run_id AND i.parked = 0)
            ORDER BY o.created_at
            """;

        using var reader = statement.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new OutboxItem(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                RemoteJson.Deserialize<HostEvent>(reader.GetString(3)), reader.GetInt32(4)));
        }

        return items;
    }

    /// <summary>Delivered, or refused with a code that says it never can be. Either way, done with.</summary>
    public void Discard(string eventId)
        => Execute("DELETE FROM outbox WHERE event_id = $id", ("$id", eventId));

    /// <summary>Refused for a reason that may pass. Counted, so it cannot be retried for ever.</summary>
    public void RecordAttempt(string eventId, string? code)
        => Execute("UPDATE outbox SET attempts = attempts + 1, last_fault = $code WHERE event_id = $id",
            ("$code", code), ("$id", eventId));

    /// <summary>
    /// Taken out of the live queue without being thrown away.
    ///
    /// <para>An unknown fault code is retried, because retrying never silently loses an event. That
    /// is only safe if something eventually stops: strict per-run ordering means one stuck event
    /// blocks every later event for that run, so past a limit it is parked, the run proceeds, and a
    /// person is told. Parked, not deleted - what could not be delivered is still readable.</para>
    /// </summary>
    public void Park(string eventId)
        => Execute("UPDATE outbox SET parked = 1 WHERE event_id = $id", ("$id", eventId));

    public IReadOnlyList<string> ParkedEventIds()
    {
        var ids = new List<string>();

        using var guard = _gate.EnterScope();
        using var statement = _connection.CreateCommand();
        statement.CommandText = "SELECT event_id FROM outbox WHERE parked = 1 ORDER BY created_at";

        using var reader = statement.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public void Dispose() { lock (_gate) _connection.Dispose(); }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>Round-trippable, sortable as text, and unambiguous about the zone.</summary>
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    private static void Bind(SqliteCommand statement, string name, object? value)
        => statement.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
        => Execute(null, sql, parameters);

    private int Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var guard = _gate.EnterScope();
        using var statement = _connection.CreateCommand();
        statement.CommandText = sql;
        statement.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            Bind(statement, name, value);
        }

        return statement.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
        => Scalar(null, sql, parameters);

    private object? Scalar(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var guard = _gate.EnterScope();
        using var statement = _connection.CreateCommand();
        statement.CommandText = sql;
        statement.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            Bind(statement, name, value);
        }

        var result = statement.ExecuteScalar();
        return result is DBNull ? null : result;
    }
}
