namespace Enactive.Engine.Tests;

using Microsoft.Data.Sqlite;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Reading a run's HEADER without reading the run.
///
/// <para><c>PLAN_v2.md</c> §11 carried it as: "<c>IRunStore.LoadAllAsync</c> reads every run whole,
/// events included, to fill a list that needs six fields. It will bite at a few hundred runs." A
/// run's events are its entire transcript - every prompt, every response, every tool call and its
/// payload - and the history column, the project-memory view, the inbox and the console's own report
/// all read every one of them to print a line each.</para>
///
/// <para>The fix is a <see cref="RunSummary"/>: a distinct TYPE, not a <see cref="RunRecord"/> with
/// an empty event list. A record that says it has no events when it has thousands is the same lie as
/// a result that says "done" for work that was refused - the caller cannot tell "none" from "not
/// loaded", and sooner or later reads one as the other.</para>
///
/// <para>The differential for the saving itself is
/// <see cref="A_summary_is_read_without_the_events_column"/>, and it is deliberately SQL-only. On
/// SQL the events are a column the query does not name, so they are never fetched at all; a file
/// store has one file per run and must still read and lex the whole thing, and what it saves is only
/// the binding of thousands of objects. Claiming otherwise for the file store would be the kind of
/// comfortable overstatement this engine exists to stop making.</para>
/// </summary>
public sealed class RunSummaryTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceInfo _workspace;

    public RunSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>MySQL is not covered: it needs a server, and a test that silently skips is worse
    /// than one that is not there. Its query is the same columns with the workspace clause the rest
    /// of that store already uses.</summary>
    public static TheoryData<string> Stores => new() { "json", "sqlite" };

    private IRunStore StoreOf(string kind)
        => kind == "json" ? new JsonRunStore(_workspace) : new SqliteRunStore(_workspace);

    private static RunRecord Record(
        string title = "a run",
        int minutesAgo = 0,
        Guid taskId = default,
        int events = 3)
    {
        var at = DateTimeOffset.Now.AddMinutes(-minutesAgo);
        var log = new List<RunEventRecord>
        {
            new(at, nameof(EventKind.IntentReceived), "Intent: " + title, null, null)
        };
        for (var i = 1; i < events; i++)
            log.Add(new RunEventRecord(at.AddSeconds(i), nameof(EventKind.ToolInvoked), $"call {i}", 1, null));

        return new RunRecord(
            Guid.NewGuid(),
            taskId == default ? Guid.NewGuid() : taskId,
            title,
            "a-model",
            at,
            at.AddSeconds(5),
            "Completed",
            log,
            new[] { "notes.md" },
            new[] { "went with the simple thing" },
            new RunSettings(2, "Assisted", "editor", Staged: false),
            new RunUsage(120, 45));
    }

    /// <summary>
    /// Field by field rather than by record equality: a record's generated <c>Equals</c> compares
    /// <c>IReadOnlyList</c> members by REFERENCE, so two summaries carrying identical artifact lists
    /// read from a store are never equal to each other. Comparing the whole thing with <c>==</c>
    /// would pass or fail for a reason that has nothing to do with what is being tested.
    /// </summary>
    private static void AssertSameHeader(RunRecord expected, RunSummary actual)
    {
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.TaskId, actual.TaskId);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.StartedAt, actual.StartedAt);
        Assert.Equal(expected.FinishedAt, actual.FinishedAt);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Artifacts, actual.Artifacts);
        Assert.Equal(expected.Decisions, actual.Decisions);
        Assert.Equal(expected.Settings, actual.Settings);
        Assert.Equal(expected.Usage, actual.Usage);
        Assert.Equal(expected.Spec, actual.Spec);
    }

    // ── the headers say the same thing the records do ───────────────────────

    /// <summary>
    /// Every field a list draws comes back with the same value it has on the record. This is the
    /// property that makes the swap safe: the caller is not being handed a cheaper, vaguer thing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_summary_carries_every_field_but_the_events(string kind)
    {
        var store = StoreOf(kind);
        var record = Record("wrote the notes");
        await store.SaveAsync(record, CancellationToken.None);

        var summary = Assert.Single(await store.LoadSummariesAsync(CancellationToken.None));

        AssertSameHeader(record, summary);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Summaries_come_back_newest_first(string kind)
    {
        var store = StoreOf(kind);
        var oldest = Record("oldest", minutesAgo: 30);
        var newest = Record("newest", minutesAgo: 1);
        var middle = Record("middle", minutesAgo: 10);

        foreach (var record in new[] { oldest, newest, middle })
            await store.SaveAsync(record, CancellationToken.None);

        var summaries = await store.LoadSummariesAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "newest", "middle", "oldest" },
            summaries.Select(s => s.Title).ToArray());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_store_with_nothing_in_it_has_no_summaries(string kind)
        => Assert.Empty(await StoreOf(kind).LoadSummariesAsync(CancellationToken.None));

    // ── the saving itself ───────────────────────────────────────────────────

    /// <summary>
    /// The differential, and the only way to observe it from outside: make the events column
    /// unreadable and see who notices.
    ///
    /// <para>With <c>events_json</c> holding text that is not JSON, <c>LoadAllAsync</c> cannot
    /// produce a record and throws. <c>LoadSummariesAsync</c> returns the run anyway - because it
    /// never asked for that column. Put <c>events_json</c> back into the summary query and this test
    /// fails with the same exception the whole-record read throws.</para>
    /// </summary>
    [Fact]
    public async Task A_summary_is_read_without_the_events_column()
    {
        var store = new SqliteRunStore(_workspace);
        var record = Record("has a long transcript");
        await store.SaveAsync(record, CancellationToken.None);

        await using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, ".enactive", "enactive.db")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET events_json = 'this is not json';";
            await command.ExecuteNonQueryAsync();
        }

        // The whole record cannot be read at all now.
        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAllAsync(CancellationToken.None));

        // The header still can, unchanged.
        var summary = Assert.Single(await store.LoadSummariesAsync(CancellationToken.None));
        Assert.Equal("has a long transcript", summary.Title);
        Assert.Equal(record.RunId, summary.RunId);
    }

    // ── one run, whole, when somebody asks for it ───────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task One_run_is_loaded_whole_by_its_id(string kind)
    {
        var store = StoreOf(kind);
        var wanted = Record("open me", minutesAgo: 5, events: 4);
        var other = Record("not me", minutesAgo: 2);

        await store.SaveAsync(other, CancellationToken.None);
        await store.SaveAsync(wanted, CancellationToken.None);

        var loaded = await store.LoadAsync(wanted.RunId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(wanted.RunId, loaded!.RunId);
        Assert.Equal("open me", loaded.Title);
        Assert.Equal(4, loaded.Events.Count);
        Assert.Equal("Intent: open me", loaded.Events[0].Summary);
    }

    /// <summary>
    /// Opening one run does not read the others. The same corruption differential, from the other
    /// side: one run's transcript is made unreadable, and its NEIGHBOUR still opens. Fall back to
    /// the default <c>LoadAsync</c> — which reads everything and picks one — and this throws.
    /// </summary>
    [Fact]
    public async Task Loading_one_run_does_not_read_the_others()
    {
        var store = new SqliteRunStore(_workspace);
        var broken = Record("its transcript is corrupt", minutesAgo: 10);
        var wanted = Record("opens anyway", minutesAgo: 1);

        await store.SaveAsync(broken, CancellationToken.None);
        await store.SaveAsync(wanted, CancellationToken.None);

        await using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, ".enactive", "enactive.db")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET events_json = 'this is not json' WHERE run_id = $id;";
            command.Parameters.AddWithValue("$id", broken.RunId.ToString());
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await store.LoadAsync(wanted.RunId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("opens anyway", loaded!.Title);
        Assert.Equal(3, loaded.Events.Count);
    }

    /// <summary>
    /// A run that is not there is null, not an empty record. The history list is drawn from headers
    /// and a row can be opened after another window has deleted the run behind it; the window shows
    /// that as "no longer in the store", which it can only do if the store says so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Loading_a_run_that_is_not_there_gives_null(string kind)
    {
        var store = StoreOf(kind);
        await store.SaveAsync(Record(), CancellationToken.None);

        Assert.Null(await store.LoadAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Loading_from_a_store_that_has_never_been_written_to_gives_null(string kind)
        => Assert.Null(await StoreOf(kind).LoadAsync(Guid.NewGuid(), CancellationToken.None));

    // ── a store that overrides neither ──────────────────────────────────────

    /// <summary>
    /// Both new methods have defaults, so a store written before they existed still compiles and
    /// still works. The default is no faster - it reads everything and throws the events away - and
    /// that is the point: it is honest rather than absent.
    /// </summary>
    [Fact]
    public async Task A_store_that_overrides_neither_still_answers_both()
    {
        IRunStore store = new OldStore();
        var record = Record("from an old store");
        await store.SaveAsync(record, CancellationToken.None);

        var summary = Assert.Single(await store.LoadSummariesAsync(CancellationToken.None));
        AssertSameHeader(record, summary);

        var loaded = await store.LoadAsync(record.RunId, CancellationToken.None);
        Assert.Equal(record.Events.Count, loaded!.Events.Count);

        Assert.Null(await store.LoadAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>An <see cref="IRunStore"/> as one looked before summaries existed.</summary>
    private sealed class OldStore : IRunStore
    {
        private readonly List<RunRecord> _records = new();

        public Task SaveAsync(RunRecord record, CancellationToken ct)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RunRecord>>(_records);

        public Task DeleteAsync(Guid runId, CancellationToken ct)
        {
            _records.RemoveAll(r => r.RunId == runId);
            return Task.CompletedTask;
        }
    }

    // ── the helpers that read a list now read headers ───────────────────────

    /// <summary>
    /// Finding a task's attempts, numbering them and naming a run all work off a header, which is what
    /// lets the history column do it without loading a single transcript. They work off a whole
    /// record too - it is the same interface - so nothing that already had one has to give it up.
    /// </summary>
    [Fact]
    public void Attempts_and_naming_work_off_a_header()
    {
        var task = Guid.NewGuid();
        var first = RunSummary.Of(Record("first go", minutesAgo: 20, taskId: task));
        var second = RunSummary.Of(Record("second go", minutesAgo: 5, taskId: task));
        var unrelated = RunSummary.Of(Record("something else", minutesAgo: 1));

        var attempts = RunHistory.AttemptsOf(new IRunHeader[] { first, second, unrelated }, second);

        Assert.Equal(2, attempts.Count);
        Assert.Equal(2, RunHistory.AttemptNumber(attempts, second.RunId));
        Assert.Equal(1, RunHistory.AttemptNumber(attempts, first.RunId));
        Assert.Equal("second go", RunTitle.For(second));
    }

    /// <summary>
    /// The project-memory view is a projection over headers. It names every run, its status, its
    /// decisions and its artifacts - and opens no transcript to do it.
    /// </summary>
    [Fact]
    public void Project_memory_renders_from_headers()
    {
        var runs = new[]
        {
            RunSummary.Of(Record("wrote the notes", minutesAgo: 5)),
            RunSummary.Of(Record("read them back", minutesAgo: 1))
        };

        var text = ProjectMemory.Render(runs, Array.Empty<Enactive.Core.Memory.MemoryEntry>(), _root);

        Assert.Contains("wrote the notes", text, StringComparison.Ordinal);
        Assert.Contains("read them back", text, StringComparison.Ordinal);
        Assert.Contains("notes.md", text, StringComparison.Ordinal);
        Assert.Contains("went with the simple thing", text, StringComparison.Ordinal);
        Assert.Contains(
            $"── Runs ({runs.Length}) ──",
            text,
            StringComparison.Ordinal);
    }
}
