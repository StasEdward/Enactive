namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Storage;
using Xunit;

/// <summary>
/// The storage layer, before anything uses it. Stage 2 of <c>Docs/REMOTE_DESIGN.md</c>, first slice.
///
/// <para>Each of these asserts something the SCHEMA does, not something the code remembers to do.
/// That distinction is the whole reason for normalised tables: a rule the database enforces cannot
/// be routed around by a future caller who did not read the comment.</para>
/// </summary>
public sealed class SchemaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private const string HostId = "11111111111111111111111111111111";
    private const string OtherHostId = "22222222222222222222222222222222";
    private const string TaskId = "33333333333333333333333333333333";
    private const string RunId = "44444444444444444444444444444444";

    /// <summary>Enough rows to hang an event off. Every test that needs a run calls this first.</summary>
    private async Task SeedAsync()
    {
        await database.ExecuteAsync($"""
            INSERT IGNORE INTO hosts (id, name, token_hash, revoked, created_at) VALUES
              ('{HostId}',      'Studio PC', REPEAT('a', 64), 0, UTC_TIMESTAMP(3)),
              ('{OtherHostId}', 'Laptop',    REPEAT('b', 64), 0, UTC_TIMESTAMP(3));
            INSERT IGNORE INTO tasks (id, host_id, workspace_id, title, prompt, created_at)
              VALUES ('{TaskId}', '{HostId}', 'workspace-1', 'Run the tests', 'Run them.', UTC_TIMESTAMP(3));
            INSERT IGNORE INTO runs (id, task_id, host_id, status, created_at)
              VALUES ('{RunId}', '{TaskId}', '{HostId}', 'Queued', UTC_TIMESTAMP(3));
            """);
    }

    /// <summary>
    /// Ordinals for events written straight to the table, bypassing the gateway that would normally
    /// allocate them. Distinct per insert because the column is UNIQUE, and that is the point: these
    /// tests are about the OTHER keys, so the ordinal must never be what refuses one of them.
    /// </summary>
    private static int _ordinal;

    private static string Event(string id, string hostId, string runId, long sequence)
        => $"""
            INSERT INTO events (id, host_id, run_id, sequence, kind, detail, at, ordinal)
            VALUES ('{id}', '{hostId}', '{runId}', {sequence}, 'Progress', 'x', UTC_TIMESTAMP(3),
                    {Interlocked.Increment(ref _ordinal)})
            """;

    // ── migrations ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_table_this_build_expects_exists()
    {
        var tables = await database.StringsAsync(
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{database.Name}'");

        Assert.Equal(
            [
                "approvals", "commands", "counters", "events", "host_workspaces", "hosts",
                "notices", "retention_state", "runs", "schema_version", "tasks"
            ],
            tables.Order().ToArray());
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
    /// written into every table.</para>
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
    /// And a database that is already current is left alone. This is the cheap half - it only
    /// exercises the version check, never the SQL - which is why it is not the test above.
    /// </summary>
    [Fact]
    public async Task An_up_to_date_database_has_nothing_applied_to_it()
        => Assert.Empty(await Migrator.ApplyAsync(database.ConnectionString));

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
        await SeedAsync();
        await database.ExecuteAsync(Event("seq-first", HostId, RunId, 500));

        var refused = await database.RefusedAsync(Event("seq-second", HostId, RunId, 500));

        Assert.NotNull(refused);
        Assert.Contains("ux_events_run_sequence", refused!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Isolation between Hosts is structural. Two machines pick event ids independently - a counter
    /// each, most likely - so the same id from two Hosts is ordinary, not an error. It is only not
    /// an error because the primary key is (host_id, id); with id alone the second Host's event
    /// would be silently rejected as a duplicate of a run it has never heard of.
    /// </summary>
    [Fact]
    public async Task Two_hosts_may_use_the_same_event_id()
    {
        await SeedAsync();
        await database.ExecuteAsync(Event("shared-id", HostId, RunId, 600));

        // Same id, different Host. The run is this Host's; who owns the run is the service's
        // question, and this test is about the key.
        Assert.Null(await database.RefusedAsync(Event("shared-id", OtherHostId, RunId, 601)));
    }

    /// <summary>An event about a run nobody created is not a timeline entry, it is a bug.</summary>
    [Fact]
    public async Task An_event_cannot_point_at_a_run_that_does_not_exist()
    {
        await SeedAsync();

        var refused = await database.RefusedAsync(
            Event("orphan", HostId, "99999999999999999999999999999999", 700));

        Assert.NotNull(refused);
        Assert.Contains("foreign key", refused!.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A command id is a string and comes back as one.
    ///
    /// <para>MySqlConnector reads a <c>CHAR(36)</c> column as a <see cref="Guid"/> - a good guess,
    /// since that is exactly a UUID's width, and wrong here: these ids are compared as text and
    /// never parsed. It surfaced as an InvalidCastException inside the command queue, nowhere near
    /// the schema that caused it. Two things now prevent it, the column type and
    /// <c>GuidFormat=None</c>, and this is what would notice if either were undone.</para>
    /// </summary>
    [Fact]
    public async Task A_command_id_is_read_back_as_the_string_it_was_written_as()
    {
        await SeedAsync();
        var commandId = Guid.NewGuid().ToString();

        await database.ExecuteAsync($"""
            INSERT INTO commands (id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES ('{commandId}', '{HostId}', 'CancelRun', '[]', REPEAT('f', 64),
                    'PendingDelivery', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """);

        Assert.Equal(commandId, Assert.Single(
            await database.StringsAsync($"SELECT id FROM commands WHERE id = '{commandId}'")));
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
        await database.ExecuteAsync($"""
            INSERT INTO hosts (id, name, token_hash, revoked, created_at)
            VALUES ('c1{new string('0', 30)}', 'Lower', REPEAT('cd', 32), 0, UTC_TIMESTAMP(3))
            """);

        var refused = await database.RefusedAsync($"""
            INSERT INTO hosts (id, name, token_hash, revoked, created_at)
            VALUES ('c2{new string('0', 30)}', 'Upper', REPEAT('CD', 32), 0, UTC_TIMESTAMP(3))
            """);

        Assert.Null(refused);

        // And the lookup that authenticates a device finds one row, not two.
        var matches = await database.ScalarAsync(
            "SELECT COUNT(*) FROM hosts WHERE token_hash = REPEAT('cd', 32)");

        Assert.Equal(1L, Convert.ToInt64(matches));
    }

    /// <summary>
    /// Text the owner and the model write is utf8mb4 - four-byte characters included. A prompt is
    /// not going to be ASCII, and a column that silently truncates at the first emoji corrupts the
    /// instruction the run is carried out from.
    /// </summary>
    [Fact]
    public async Task Text_survives_characters_outside_the_basic_plane()
    {
        await SeedAsync();
        const string prompt = "Проверь тесты 🧪 и 漢字";

        await database.ExecuteAsync($"""
            INSERT INTO tasks (id, host_id, workspace_id, title, prompt, created_at)
            VALUES ('{new string('7', 32)}', '{HostId}', 'workspace-1', 'Unicode', '{prompt}', UTC_TIMESTAMP(3))
            """);

        var stored = await database.StringsAsync(
            $"SELECT prompt FROM tasks WHERE id = '{new string('7', 32)}'");

        Assert.Equal(prompt, Assert.Single(stored));
    }
}
