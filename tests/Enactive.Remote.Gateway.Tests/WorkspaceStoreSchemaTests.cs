namespace Enactive.Remote.Gateway.Tests;

using System.Runtime.ExceptionServices;
using Enactive.Core.Context;
using Enactive.Workspace;
using MySqlConnector;

/// <summary>
/// The workspace's MySQL stores open an existing database without trying to add what it already has, and still
/// upgrade one made before their columns existed.
///
/// <para>They ran every ALTER and CREATE INDEX each time and caught any MySqlException as "already there": a
/// "Duplicate column name" thrown on every start, and any other failure of the statement taken for success
/// (2026-10-05, found with the same defect in the SQLite stores). The exception was caught, so only watching for it
/// as it is thrown shows it - for the code under test and nothing running beside it.</para>
/// </summary>
public sealed class WorkspaceStoreSchemaTests : IAsyncLifetime
{
    // Its own empty database: the gateway's schema has a "runs" table of its own.
    private readonly TestDatabase _database = new(migrate: false);
    private readonly WorkspaceInfo _workspace =
        WorkspaceInfo.For(Path.Combine(Path.GetTempPath(), "enactive-mysql-schema", Guid.NewGuid().ToString("N")));

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    private static readonly AsyncLocal<bool> Watching = new();

    /// <summary>Every MySqlException thrown inside <paramref name="act"/>, caught or not.</summary>
    private static async Task<List<string>> ThrownWhile(Func<Task> act)
    {
        var thrown = new List<string>();
        void Seen(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (Watching.Value && e.Exception is MySqlException mysql)
                lock (thrown) thrown.Add(mysql.Message);
        }

        AppDomain.CurrentDomain.FirstChanceException += Seen;
        try
        {
            Watching.Value = true;
            await act();
        }
        finally
        {
            Watching.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= Seen;
        }
        return thrown;
    }

    private async Task OpenBothStoresAsync()
    {
        await new MySqlRunStore(_database.ConnectionString, _workspace.Id).LoadSummariesAsync(CancellationToken.None);
        await new MySqlInboxStore(_database.ConnectionString, _workspace).LoadAllAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_existing_database_opens_without_an_error()
    {
        // The first opening creates the tables; the second finds them complete, as every later start does.
        var thrown = await ThrownWhile(async () =>
        {
            await OpenBothStoresAsync();
            await OpenBothStoresAsync();
        });

        Assert.Empty(thrown);
    }

    [Fact]
    public async Task A_database_from_before_the_columns_is_upgraded()
    {
        await _database.ExecuteAsync(
            """
            CREATE TABLE runs (
              run_id         CHAR(36) PRIMARY KEY,
              task_id        CHAR(36),
              title          TEXT,
              model          VARCHAR(255),
              started_at     VARCHAR(40),
              finished_at    VARCHAR(40),
              status         VARCHAR(40),
              events_json    LONGTEXT,
              artifacts_json LONGTEXT,
              decisions_json LONGTEXT
            );
            """);
        await _database.ExecuteAsync(
            """
            CREATE TABLE inbox (
              id           CHAR(36) PRIMARY KEY,
              workspace_id CHAR(36),
              kind         VARCHAR(64),
              title        TEXT,
              summary      LONGTEXT,
              run_id       CHAR(36),
              status       VARCHAR(16),
              at           VARCHAR(40),
              INDEX ix_inbox_workspace (workspace_id, at)
            );
            """);

        await OpenBothStoresAsync();

        var columns = await _database.StringsAsync(
            "SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()");
        foreach (var expected in new[] { "runs.settings_json", "runs.usage_json", "runs.spec_json", "runs.checks_json", "runs.workspace_id", "inbox.schedule_id" })
            Assert.Contains(expected, columns);

        Assert.Equal(1, await _database.ScalarLongAsync(
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'runs' AND INDEX_NAME = 'ix_runs_workspace'"));
    }

    /// <summary>
    /// Plan Phase 3.1: a run's checks travel whole through the MySQL store - by name for their enums - and a run recorded
    /// without them reads back without them.
    /// </summary>
    [Fact]
    public async Task A_run_record_keeps_its_checks_through_mysql()
    {
        var store = new MySqlRunStore(_database.ConnectionString, _workspace.Id);
        var lint = new Enactive.Core.Checks.CheckDefinition("lint", "Lints", "npm run lint", ["src/**/*.ts"],
            Enactive.Core.Checks.CheckFire.Step, Enactive.Core.Checks.CheckSeverity.Blocking, 45, ["tests/**"]);
        var checks = new Enactive.Core.Checks.CheckSnapshot(
            [new Enactive.Core.Checks.ResolvedCheck(lint, Enactive.Core.Checks.CheckOrigin.Workspace)],
            [new Enactive.Core.Checks.CheckManifestHash("lint.json", new string('a', 64))]);
        Enactive.Core.History.RunRecord Record() => new(Guid.NewGuid(), Guid.NewGuid(), "a run", "model",
            DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now, "Completed", [], [], []);
        var with = Record() with { CheckSnapshot = checks };
        var without = Record();

        await store.SaveAsync(with, CancellationToken.None);
        await store.SaveAsync(without, CancellationToken.None);

        var back = (await store.LoadAsync(with.RunId, CancellationToken.None))!.CheckSnapshot!;
        Assert.Equal(lint, Assert.Single(back.Checks).Definition);
        Assert.Equal("lint.json", Assert.Single(back.Manifests).File);
        Assert.Null((await store.LoadAsync(without.RunId, CancellationToken.None))!.CheckSnapshot);
        Assert.Contains("\"Blocking\"", await _database.StringsAsync(
            $"SELECT checks_json FROM runs WHERE run_id = '{with.RunId}'") is [var json] ? json : "", StringComparison.Ordinal);
    }

    /// <summary>Deleting a run from MySQL removes what its checks left on the workspace's disk (Plan Phase 3.3), as the other stores do.</summary>
    [Fact]
    public async Task Deleting_a_run_through_mysql_removes_its_check_records()
    {
        var store = new MySqlRunStore(_database.ConnectionString, _workspace.Id, _workspace.RootPath);
        var run = new Enactive.Core.History.RunRecord(Guid.NewGuid(), Guid.NewGuid(), "a run", "model",
            DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now, "Completed", [], [], []);
        await store.SaveAsync(run, CancellationToken.None);
        var folder = Path.Combine(_workspace.RootPath, ".enactive", "checks", "runs", run.RunId.ToString("D"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "step-1-lint.txt"), "output");

        await store.DeleteAsync(run.RunId, CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        Assert.Null(await store.LoadAsync(run.RunId, CancellationToken.None));
    }
}
