namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;
using Xunit;

/// <summary>
/// The storage layer, before anything uses it.
///
/// <para>Each of these asserts something the SCHEMA does, not something the code remembers to do.
/// That distinction is the whole reason for composite keys: a rule the database enforces cannot be
/// routed around by a future caller who did not read the comment. The one thing the schema cannot do
/// is protect a SELECT that forgets its owner filter - the isolation tests of the services do.</para>
/// </summary>
public sealed class SchemaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    /// <summary>
    /// One user with a computer, a task on it and a run of that task: enough to hang anything else
    /// off. Ids are generated per call, so tests in this class share a database without sharing rows.
    /// </summary>
    private sealed record Seeded(string UserId, string HostId, string TaskId, string RunId);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<Seeded> SeedAsync(string displayName = "Alice")
    {
        var seeded = new Seeded(NewId(), NewId(), Guid.NewGuid().ToString(), NewId());

        await database.ExecuteAsync($"""
            INSERT INTO users (id, display_name, status, created_at)
              VALUES ('{seeded.UserId}', '{displayName}', 'Active', UTC_TIMESTAMP(3));
            INSERT INTO hosts (id, owner_id, label, token_hash, created_at)
              VALUES ('{seeded.HostId}', '{seeded.UserId}', 'Studio PC', '{Hash()}', UTC_TIMESTAMP(3));
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
              VALUES ('{seeded.UserId}', '{seeded.TaskId}', '{seeded.HostId}', 'workspace-1',
                      'e1:AAAA', REPEAT('f', 64), UTC_TIMESTAMP(3));
            INSERT INTO runs (id, owner_id, task_id, host_id, status, created_at)
              VALUES ('{seeded.RunId}', '{seeded.UserId}', '{seeded.TaskId}', '{seeded.HostId}',
                      'Queued', UTC_TIMESTAMP(3));
            """);

        return seeded;
    }

    /// <summary>A token hash no other row has: the column is UNIQUE, and these tests are about the OTHER keys.</summary>
    private static string Hash() => NewId() + NewId();

    private static int _ordinal;

    /// <summary>
    /// Ordinals for events written straight to the table, bypassing the gateway that would allocate
    /// them. Distinct per insert unless a test says otherwise, because the key is UNIQUE per owner and
    /// these tests are about the OTHER keys.
    /// </summary>
    private static string Event(
        Seeded of, string id, long sequence, long? ordinal = null, string? hostId = null, string? runId = null)
        => $"""
            INSERT INTO events (owner_id, host_id, id, run_id, sequence, kind, sealed_detail, at, ordinal)
            VALUES ('{of.UserId}', '{hostId ?? of.HostId}', '{id}', '{runId ?? of.RunId}', {sequence},
                    'Progress', 'e1:AAAA', UTC_TIMESTAMP(3), {ordinal ?? Interlocked.Increment(ref _ordinal)})
            """;

    private static string Command(Seeded of, string id)
        => $"""
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES ('{of.UserId}', '{id}', '{of.HostId}', 'CancelRun', '[]', REPEAT('f', 64),
                    'PendingDelivery', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """;

    // ── migrations ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_table_this_build_expects_exists()
    {
        var tables = await database.StringsAsync(
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{database.Name}'");

        Assert.Equal(
            new[]
            {
                "admissions", "approvals", "audit", "commands", "devices", "enrollments", "events",
                "external_identities", "grants", "host_workspaces", "hosts", "invites", "notices", "runs",
                "schema_version", "tasks", "user_retention", "user_sessions", "user_streams", "users"
            }.Order(StringComparer.Ordinal),
            tables.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The rule <see cref="Migrator"/> depends on and cannot enforce by itself.
    ///
    /// <para>MySQL commits DDL implicitly, so a migration that fails half way through has already
    /// half happened and its version row was never written. Recovery is re-running it - which works
    /// only because every statement is written to be harmless the second time. This is what checks
    /// that a future migration keeps that promise, and it fails the moment one is added with a bare
    /// <c>CREATE TABLE</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_migration_is_safe_to_apply_a_second_time()
    {
        // The only state a failed migration can leave behind: the tables exist and nothing records
        // that they do, because the version row is written last and never got written. Removing the
        // row reproduces it exactly, and the recovery is to run the file again over its own work.
        await database.ExecuteAsync("DELETE FROM schema_version");

        var reapplied = await Migrator.ApplyAsync(database.ConnectionString);

        Assert.Equal([1], Migrator.KnownVersions());
        Assert.Equal(Migrator.KnownVersions(), reapplied);
        Assert.Equal(
            Migrator.KnownVersions(),
            await database.IntsAsync("SELECT version FROM schema_version ORDER BY version"));
    }

    /// <summary>
    /// The server is one of the series this schema was written against.
    ///
    /// <para>Everything else in this file asks what the database refuses. This asks what database
    /// it is - because a suite pointed at 8.4, or at MariaDB, would still pass most of these and
    /// would be proving it about a server production does not run. What changes between series is
    /// the set of collations and a good deal of SQL behaviour, and <c>utf8mb4_0900_ai_ci</c> is
    /// the collation every table here declares.</para>
    ///
    /// <para>Here rather than in the CI workflow on purpose: a check in the pipeline runs only in
    /// the pipeline, and would need a mysql client on the runner image. This one runs wherever the
    /// tests do, including on the machine of whoever points them somewhere new.</para>
    /// </summary>
    [Fact]
    public async Task The_server_is_the_series_this_schema_was_written_for()
    {
        var version = Assert.Single(await database.StringsAsync("SELECT VERSION()"));

        Assert.StartsWith("8.0.", version, StringComparison.Ordinal);
    }

    /// <summary>
    /// A database that records a schema version this build has no migration for was written by another protocol
    /// of the gateway - protocol 1's database records version 2, which this build does not ship. Started on it,
    /// the gateway passed over the versions it did not know, started, and answered its health check while every
    /// call failed on tables of another shape: the install was reported a success and the service was down for
    /// everyone. It refuses to start instead, with what to do, and changes nothing.
    /// </summary>
    [Fact]
    public async Task A_database_of_another_protocol_stops_the_start_with_the_cutovers_instructions()
    {
        await database.WithScratchDatabaseAsync("utf8mb4", "utf8mb4_0900_ai_ci", async connectionString =>
        {
            var unknown = Migrator.KnownVersions().Max() + 1;
            await using (var connection = new MySqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new MySqlCommand(
                    $"INSERT INTO schema_version (version, applied_at) VALUES ({unknown}, UTC_TIMESTAMP(3))", connection);
                await command.ExecuteNonQueryAsync();
            }

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Migrator.ApplyAsync(connectionString));

            Assert.Equal(
                $"This database was written by another protocol of Enactive Remote (schema version {unknown}). "
                + "Install by hand: Docs/REMOTE_OPERATIONS.md §cutover.",
                refused.Message);
        });
    }

    /// <summary>
    /// And a database that is already current is left alone. This is the cheap half - it only
    /// exercises the version check, never the SQL - which is why it is not the test above.
    /// </summary>
    [Fact]
    public async Task An_up_to_date_database_has_nothing_applied_to_it()
        => Assert.Empty(await Migrator.ApplyAsync(database.ConnectionString));

    // ── ownership, which the database decides ───────────────────────────────

    /// <summary>
    /// The reason every child names its owner and every foreign key includes it.
    ///
    /// <para>A service that forgets to filter a WRITE by owner would otherwise attach Bob's task to
    /// Alice's computer, and the database would accept it: the host id alone is a valid reference.
    /// With <c>(owner_id, host_id)</c> as the reference, Bob's row finds no parent.</para>
    /// </summary>
    [Fact]
    public async Task A_child_with_another_owner_is_refused_by_the_database()
    {
        var alice = await SeedAsync("Alice");
        var bob = await SeedAsync("Bob");

        var refused = await database.RefusedAsync($"""
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
            VALUES ('{bob.UserId}', '{Guid.NewGuid()}', '{alice.HostId}', 'workspace-1',
                    'e1:AAAA', REPEAT('f', 64), UTC_TIMESTAMP(3))
            """);

        Assert.NotNull(refused);
        Assert.Contains("fk_tasks_host", refused!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing a person removes everything they put on the service, in one statement, with nothing
    /// left behind to be found by somebody else. Seeded in EVERY owned table, because a table added
    /// later without a cascade is exactly the one that would keep a stranger's data.
    /// </summary>
    [Fact]
    public async Task Deleting_a_user_deletes_everything_they_own()
    {
        var alice = await SeedAsync("Alice");
        var device = NewId();
        var invite = NewId();
        var approval = NewId();

        await database.ExecuteAsync($"""
            INSERT INTO external_identities (provider, subject, user_id, display, created_at)
              VALUES ('github', '{alice.UserId}', '{alice.UserId}', 'alice', UTC_TIMESTAMP(3));
            INSERT INTO user_sessions (id, user_id, security_version, created_at, expires_at)
              VALUES ('{NewId()}', '{alice.UserId}', 1, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY);
            INSERT INTO user_streams (owner_id) VALUES ('{alice.UserId}');
            INSERT INTO user_retention (owner_id) VALUES ('{alice.UserId}');
            INSERT INTO devices (id, owner_id, public_key, label, created_at)
              VALUES ('{device}', '{alice.UserId}', REPEAT(0x04, 65), 'Phone', UTC_TIMESTAMP(3));
            INSERT INTO host_workspaces (owner_id, host_id, workspace_id, sealed_name)
              VALUES ('{alice.UserId}', '{alice.HostId}', 'workspace-1', 'e1:AAAA');
            INSERT INTO grants (owner_id, host_id, device_id, epoch, grant_json, created_at)
              VALUES ('{alice.UserId}', '{alice.HostId}', '{device}', 1, '[]', UTC_TIMESTAMP(3));
            INSERT INTO invites (id, owner_id, created_at, expires_at)
              VALUES ('{invite}', '{alice.UserId}', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY);
            INSERT INTO enrollments (invite_id, owner_id, device_id, mac, created_at)
              VALUES ('{invite}', '{alice.UserId}', '{device}', 'mac', UTC_TIMESTAMP(3));
            INSERT INTO approvals (owner_id, host_id, id, run_id, tool_call_id, action_hash,
                                   remote_decidable, sealed_action, status, created_at, expires_at)
              VALUES ('{alice.UserId}', '{alice.HostId}', '{approval}', '{alice.RunId}', 'call-1',
                      REPEAT('a', 64), 1, 'e1:AAAA', 'Pending', UTC_TIMESTAMP(3),
                      UTC_TIMESTAMP(3) + INTERVAL 1 DAY);
            INSERT INTO notices (id, owner_id, run_id, kind, at, ordinal)
              VALUES ('{NewId()}', '{alice.UserId}', '{alice.RunId}', 'Completed', UTC_TIMESTAMP(3), 1);
            INSERT INTO audit (owner_id, at, actor, action)
              VALUES ('{alice.UserId}', UTC_TIMESTAMP(3), 'user:{alice.UserId}', 'sign-in');
            """);
        await database.ExecuteAsync(Command(alice, Guid.NewGuid().ToString()));
        await database.ExecuteAsync(Event(alice, "event-1", 1));

        // (table, the column that names its owner). `users` itself is the root.
        (string Table, string Column)[] owned =
        [
            ("external_identities", "user_id"), ("user_sessions", "user_id"), ("user_streams", "owner_id"),
            ("user_retention", "owner_id"), ("devices", "owner_id"), ("hosts", "owner_id"),
            ("host_workspaces", "owner_id"), ("grants", "owner_id"), ("invites", "owner_id"),
            ("enrollments", "owner_id"), ("tasks", "owner_id"), ("runs", "owner_id"),
            ("commands", "owner_id"), ("approvals", "owner_id"), ("events", "owner_id"),
            ("notices", "owner_id"), ("audit", "owner_id")
        ];

        // Seeded for real first: a table that was never populated would "count zero" for the wrong reason.
        foreach (var (table, column) in owned)
        {
            Assert.True(
                await database.ScalarLongAsync($"SELECT COUNT(*) FROM {table} WHERE {column} = '{alice.UserId}'") > 0,
                $"{table} was not seeded.");
        }

        await database.ExecuteAsync($"DELETE FROM users WHERE id = '{alice.UserId}'");

        foreach (var (table, column) in owned)
        {
            Assert.Equal(
                0L,
                await database.ScalarLongAsync($"SELECT COUNT(*) FROM {table} WHERE {column} = '{alice.UserId}'"));
        }
    }

    /// <summary>
    /// Ids are chosen by the browser and the computer, not by the gateway, so two people choosing
    /// the same one is ordinary. It is only not an error because the key starts with the owner; with
    /// the id alone, Bob's command would be refused as a duplicate of one he has never heard of -
    /// which also tells him that Alice used that id.
    /// </summary>
    [Fact]
    public async Task Two_owners_may_use_the_same_command_and_task_ids()
    {
        var alice = await SeedAsync("Alice");
        var bob = await SeedAsync("Bob");
        var commandId = Guid.NewGuid().ToString();

        await database.ExecuteAsync(Command(alice, commandId));
        Assert.Null(await database.RefusedAsync(Command(bob, commandId)));

        // The same task id on Bob's own computer.
        Assert.Null(await database.RefusedAsync($"""
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
            VALUES ('{bob.UserId}', '{alice.TaskId}', '{bob.HostId}', 'workspace-1',
                    'e1:AAAA', REPEAT('f', 64), UTC_TIMESTAMP(3))
            """));

        // And one owner still cannot use an id twice: that is what a retry looks like.
        var repeated = await database.RefusedAsync(Command(alice, commandId));

        Assert.NotNull(repeated);
        Assert.Contains("Duplicate entry", repeated!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The number line a panel's delta poll is ordered by belongs to one person. A global line would
    /// make every owner's events contend for one counter and would let a gap in somebody else's
    /// numbers show up in yours; per owner, the only thing a number must be is unique among that
    /// owner's own rows.
    /// </summary>
    [Fact]
    public async Task Event_ordinals_are_unique_per_owner_not_globally()
    {
        var alice = await SeedAsync("Alice");
        var bob = await SeedAsync("Bob");

        await database.ExecuteAsync(Event(alice, "first", 1, ordinal: 1));

        Assert.Null(await database.RefusedAsync(Event(bob, "first", 1, ordinal: 1)));

        var refused = await database.RefusedAsync(Event(alice, "second", 2, ordinal: 1));

        Assert.NotNull(refused);
        Assert.Contains("ux_events_owner_ordinal", refused!.Message, StringComparison.Ordinal);
    }

    // ── ordering, which the database decides ────────────────────────────────

    /// <summary>
    /// The invariant deduplication by event id does NOT provide.
    ///
    /// <para>Two events, two ids, one sequence. Without <c>UNIQUE (run_id, sequence)</c> both land
    /// and the run's state depends on which arrived last - which, with retries, is not the order
    /// they were written in. A retried event overtaking a later one drives a run backwards, and the
    /// panel shows a finished run as running again.</para>
    /// </summary>
    [Fact]
    public async Task One_sequence_number_belongs_to_one_event()
    {
        var alice = await SeedAsync();
        await database.ExecuteAsync(Event(alice, "seq-first", 500));

        var refused = await database.RefusedAsync(Event(alice, "seq-second", 500));

        Assert.NotNull(refused);
        Assert.Contains("ux_events_run_sequence", refused!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Isolation between computers is structural. Two machines pick event ids independently - a
    /// counter each, most likely - so the same id from two of them is ordinary, not an error. It is
    /// only not an error because the primary key is (host_id, id); with id alone the second
    /// computer's event would be silently rejected as a duplicate of a run it has never heard of.
    /// </summary>
    [Fact]
    public async Task Two_hosts_may_use_the_same_event_id()
    {
        var first = await SeedAsync();

        // A second computer of the SAME person, with a run of its own.
        var otherHost = NewId();
        var otherTask = Guid.NewGuid().ToString();
        var otherRun = NewId();

        await database.ExecuteAsync($"""
            INSERT INTO hosts (id, owner_id, label, token_hash, created_at)
              VALUES ('{otherHost}', '{first.UserId}', 'Laptop', '{Hash()}', UTC_TIMESTAMP(3));
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
              VALUES ('{first.UserId}', '{otherTask}', '{otherHost}', 'workspace-1',
                      'e1:AAAA', REPEAT('f', 64), UTC_TIMESTAMP(3));
            INSERT INTO runs (id, owner_id, task_id, host_id, status, created_at)
              VALUES ('{otherRun}', '{first.UserId}', '{otherTask}', '{otherHost}', 'Queued', UTC_TIMESTAMP(3));
            """);

        await database.ExecuteAsync(Event(first, "shared-id", 600));

        Assert.Null(await database.RefusedAsync(
            Event(first, "shared-id", 601, hostId: otherHost, runId: otherRun)));
    }

    /// <summary>An event about a run nobody created is not a timeline entry, it is a bug.</summary>
    [Fact]
    public async Task An_event_cannot_point_at_a_run_that_does_not_exist()
    {
        var alice = await SeedAsync();

        var refused = await database.RefusedAsync(
            Event(alice, "orphan", 700, runId: "99999999999999999999999999999999"));

        Assert.NotNull(refused);
        Assert.Contains("foreign key", refused!.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A task id is a string and comes back as one.
    ///
    /// <para>MySqlConnector reads a <c>CHAR(36)</c> column as a <see cref="Guid"/> - a good guess,
    /// since that is exactly a UUID's width, and wrong here: these ids are compared as text and
    /// never parsed. It surfaced as an InvalidCastException inside the command queue, nowhere near
    /// the schema that caused it. <c>GuidFormat=None</c> now prevents it, and this is what would
    /// notice if that were undone. The task id is the one column that is still a CHAR(36).</para>
    /// </summary>
    [Fact]
    public async Task A_task_id_is_read_back_as_the_string_it_was_written_as()
    {
        var alice = await SeedAsync();

        Assert.Equal(alice.TaskId, Assert.Single(
            await database.StringsAsync($"SELECT id FROM tasks WHERE owner_id = '{alice.UserId}'")));
    }

    // ── collation, which decides what "equal" means ─────────────────────────

    /// <summary>
    /// Why the hash columns are <c>ascii_bin</c> and not the database's default.
    ///
    /// <para>Under <c>utf8mb4_0900_ai_ci</c> these two hashes are the same value, so the unique key
    /// refuses the second row - and, far worse, <c>WHERE token_hash = ?</c> matches a hash that was
    /// never issued. That comparison is what authenticates a device.</para>
    /// </summary>
    [Fact]
    public async Task Two_hashes_differing_only_in_case_are_two_hashes()
    {
        var alice = await SeedAsync();
        var lower = $"{NewId()}{NewId()}".Replace('0', 'c');
        var upper = lower.ToUpperInvariant();

        await database.ExecuteAsync($"""
            INSERT INTO hosts (id, owner_id, label, token_hash, created_at)
            VALUES ('{NewId()}', '{alice.UserId}', 'Lower', '{lower}', UTC_TIMESTAMP(3))
            """);

        var refused = await database.RefusedAsync($"""
            INSERT INTO hosts (id, owner_id, label, token_hash, created_at)
            VALUES ('{NewId()}', '{alice.UserId}', 'Upper', '{upper}', UTC_TIMESTAMP(3))
            """);

        Assert.Null(refused);

        // And the lookup that authenticates a device finds one row, not two.
        Assert.Equal(1L, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM hosts WHERE token_hash = '{lower}'"));
    }

    /// <summary>
    /// Text a person writes in the clear - a display name, a computer's label - is utf8mb4,
    /// four-byte characters included. A column that silently truncates at the first emoji corrupts
    /// what the person typed.
    /// </summary>
    [Fact]
    public async Task Text_survives_characters_outside_the_basic_plane()
    {
        const string name = "Проверь тесты 🧪 и 漢字";
        var alice = await SeedAsync(name);

        Assert.Equal(name, Assert.Single(
            await database.StringsAsync($"SELECT display_name FROM users WHERE id = '{alice.UserId}'")));
    }

    /// <summary>
    /// Why sealed columns are <c>ascii</c>: an envelope is <c>e1:</c> plus base64url, so a sentence a
    /// person typed in their own language cannot be one. The database refuses such text at the first
    /// insert instead of storing it. It does NOT refuse ASCII plaintext - an English sentence fits an
    /// ascii column - so this is a net for a whole class of mistakes, not a check that a value is an
    /// envelope; the services validate the envelope's shape.
    /// </summary>
    [Fact]
    public async Task A_sealed_column_refuses_non_ascii_text()
    {
        var alice = await SeedAsync();

        var refused = await database.RefusedAsync($"""
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
            VALUES ('{alice.UserId}', '{Guid.NewGuid()}', '{alice.HostId}', 'workspace-1',
                    'Проверь тесты', REPEAT('f', 64), UTC_TIMESTAMP(3))
            """);

        Assert.NotNull(refused);
        Assert.Contains("Incorrect string value", refused!.Message, StringComparison.Ordinal);
    }

    // ── what retention reads through ────────────────────────────────────────

    /// <summary>
    /// Retention deletes one person's oldest rows a batch at a time. Through a key on (owner, time)
    /// that batch is the first stretch of one range of the index, read in order. Without one, MySQL
    /// read every row the person had, sorted them, and locked each row it read until the batch
    /// committed - a batch "of a thousand" holding locks on all of an account's history while that
    /// account was writing to it.
    /// </summary>
    [Theory]
    [InlineData("events", "ix_events_owner_at")]
    [InlineData("notices", "ix_notices_owner_at")]
    public async Task Retention_deletes_through_the_owners_time_key(string table, string key)
    {
        var alice = await SeedAsync("Alice");

        foreach (var owner in new[] { alice, await SeedAsync("Bob") })
        {
            // Two hundred rows a day apart for each of two people, most of them past the cutoff: the
            // first pass over a neglected account, where the optimizer, left to itself, read the whole
            // table and sorted it even with the key there.
            await database.ExecuteAsync(table == "events"
                ? $"""
                  INSERT INTO events (owner_id, host_id, id, run_id, sequence, kind, sealed_detail, at, ordinal)
                  WITH RECURSIVE n (i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 200)
                  SELECT '{owner.UserId}', '{owner.HostId}', CONCAT('retention-', i), '{owner.RunId}', i,
                         'Progress', 'e1:AAAA', UTC_TIMESTAMP(3) - INTERVAL i DAY, 100000 + i
                  FROM n
                  """
                : $"""
                  INSERT INTO notices (id, owner_id, run_id, kind, at, is_read, ordinal)
                  WITH RECURSIVE n (i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 200)
                  SELECT REPLACE(UUID(), '-', ''), '{owner.UserId}', '{owner.RunId}', 'Completed',
                         UTC_TIMESTAMP(3) - INTERVAL i DAY, i % 2, i
                  FROM n
                  """);
        }

        await database.ExecuteAsync($"ANALYZE TABLE {table}");

        // The batch is measured before it is deleted, so both statements must read through the key.
        var trimmed = table == "events" ? Retention.Events : Retention.Notices;

        foreach (var statement in new[] { Retention.DeleteBatch(trimmed), Retention.MeasureBatch(trimmed) })
        {
            await using var connection = await database.OpenAsync();
            await using var explain = new MySqlConnector.MySqlCommand("EXPLAIN " + statement, connection);
            explain.Parameters.AddWithValue("@owner", alice.UserId);
            explain.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-30));
            await using var plan = await explain.ExecuteReaderAsync();

            Assert.True(await plan.ReadAsync());
            var extra = plan["Extra"] as string ?? "";
            Assert.Equal(key, plan["key"] as string);
            Assert.DoesNotContain("filesort", extra, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Every table declares its own collation, so the schema does not depend on the database it is
    /// created in. The other tests run in a utf8mb4 database, where a table that forgot the clause
    /// would inherit the right answer by luck; a latin1 default is where it would not. Columns such as
    /// <c>users.display_name</c> and <c>hosts.label</c> name no charset of their own, so a table that
    /// took latin1 would turn a person's non-ASCII name into question marks.
    /// </summary>
    [Fact]
    public async Task Every_table_is_utf8mb4_whatever_the_database_default()
        => await database.WithScratchDatabaseAsync("latin1", "latin1_swedish_ci", async connectionString =>
        {
            var schema = new MySqlConnector.MySqlConnectionStringBuilder(connectionString).Database;
            await using var connection = new MySqlConnector.MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlConnector.MySqlCommand(
                $"SELECT table_name, table_collation FROM information_schema.tables WHERE table_schema = '{schema}'",
                connection);
            await using var reader = await command.ExecuteReaderAsync();

            var tables = new List<(string Name, string Collation)>();
            while (await reader.ReadAsync())
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }

            Assert.NotEmpty(tables);
            Assert.All(tables, table => Assert.True(
                table.Collation == "utf8mb4_0900_ai_ci",
                $"{table.Name} is {table.Collation}."));
        });
}
