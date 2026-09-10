namespace Enactive.Engine.Tests;

using Microsoft.Data.Sqlite;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The schedule column, through the store that actually has columns.
///
/// <para>The migration is the part worth testing, not the round trip. The store's reads and writes
/// are wrapped in <c>catch { }</c> — the inbox is never load-bearing — so a SELECT naming a column
/// that is not there does not fail loudly: it returns NOTHING, and every workspace that has been
/// using Enactive until today reports an empty inbox. "A read error looked like an empty store" is a
/// defect this codebase has already had once, in the file stores, and it is worth not having
/// again.</para>
/// </summary>
public sealed class InboxScheduleColumnTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-inboxcol", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceInfo _workspace;

    public InboxScheduleColumnTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string DatabasePath => Path.Combine(_root, ".enactive", "enactive.db");

    private InboxItem Item(string title, Guid? schedule)
        => new(Guid.NewGuid(), _workspace.Id, "result", title, "summary", Guid.NewGuid(),
               "unread", DateTimeOffset.UtcNow, schedule);

    /// <summary>The inbox table exactly as it was before the column, with one row already in it.</summary>
    private void CreateTheOldTableWithARow(string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE inbox (
                  id           TEXT PRIMARY KEY,
                  workspace_id TEXT,
                  kind         TEXT,
                  title        TEXT,
                  summary      TEXT,
                  run_id       TEXT,
                  status       TEXT,
                  at           TEXT
                );
                """;
            create.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO inbox (id, workspace_id, kind, title, summary, run_id, status, at)
            VALUES ($id, $w, 'result', $title, 'from before', $run, 'unread', $at);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("$w", _workspace.Id.ToString());
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$run", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("o"));
        insert.ExecuteNonQuery();
    }

    [Fact]
    public async Task An_inbox_written_before_the_column_existed_still_reads()
    {
        CreateTheOldTableWithARow("filed last week");

        var items = await new SqliteInboxStore(_workspace).LoadAllAsync(CancellationToken.None);

        Assert.Single(items);
        Assert.Equal("filed last week", items[0].Title);
    }

    /// <summary>
    /// And that row says it came from NO schedule, rather than from a schedule with an empty id.
    /// The two are different questions and only one of them is worth going to look for.
    /// </summary>
    [Fact]
    public async Task A_row_from_before_reports_no_schedule_rather_than_an_empty_one()
    {
        CreateTheOldTableWithARow("filed last week");

        var items = await new SqliteInboxStore(_workspace).LoadAllAsync(CancellationToken.None);

        Assert.Null(items[0].ScheduleId);
    }

    /// <summary>An upgraded table takes new rows, with the id in the new column.</summary>
    [Fact]
    public async Task The_schedule_survives_the_upgrade_and_a_round_trip()
    {
        CreateTheOldTableWithARow("filed last week");

        var store = new SqliteInboxStore(_workspace);
        var schedule = Guid.NewGuid();
        await store.AppendAsync(Item("filed tonight", schedule), CancellationToken.None);

        var items = await store.LoadAllAsync(CancellationToken.None);
        var scheduled = Assert.Single(items, i => i.Title == "filed tonight");

        Assert.Equal(schedule, scheduled.ScheduleId);
        Assert.Equal(2, items.Count);
    }

    /// <summary>A fresh database, with no upgrade involved, behaves the same way.</summary>
    [Fact]
    public async Task A_new_inbox_keeps_the_schedule_and_keeps_null_null()
    {
        var store = new SqliteInboxStore(_workspace);
        var schedule = Guid.NewGuid();

        await store.AppendAsync(Item("scheduled", schedule), CancellationToken.None);
        await store.AppendAsync(Item("typed", null), CancellationToken.None);

        var items = await store.LoadAllAsync(CancellationToken.None);

        Assert.Equal(schedule, Assert.Single(items, i => i.Title == "scheduled").ScheduleId);
        Assert.Null(Assert.Single(items, i => i.Title == "typed").ScheduleId);
    }
}
