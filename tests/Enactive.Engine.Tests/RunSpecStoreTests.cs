namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// M3a of the task-templates plan: a run remembers the specification it ran under.
///
/// <para>Without it, reading a finished run means reading the template as it is TODAY - and a
/// template is editable, so that answers "what would this do now" rather than "what did it do".
/// The run record already keeps the SETTINGS a run had for exactly that reason; this is the same
/// idea one level up.</para>
/// </summary>
public sealed class RunSpecStoreTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceInfo _workspace;

    public RunSpecStoreTests()
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

    private static ResolvedTaskSpec Spec() => new(
        TemplateId: "release-check",
        TemplateVersion: 4,
        TemplateName: "Release Check",
        WorkspaceId: Guid.NewGuid(),
        WorkspaceName: "Enactive",
        WorkspaceRoot: @"c:\repos\Enactive",
        Goal: "Build Enactive.sln and report.",
        Parameters: new Dictionary<string, string> { ["solution"] = "Enactive.sln" },
        Permissions: new PermissionPolicy(
            PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" }) { Deny = new[] { "git_push" } },
        SuccessCriteria: new[] { new SuccessCriterionDefinition("Builds", "dotnet build", 0) },
        Limits: new ExecutionLimits(MaxSteps: 12),
        WorkerId: "developer",
        ReviewRequired: true);

    private static RunRecord Record(string? spec) => new(
        Guid.NewGuid(), Guid.NewGuid(), "a run", "fake-model",
        DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now, "Completed",
        Array.Empty<RunEventRecord>(), Array.Empty<string>(), Array.Empty<string>(),
        Settings: null, Usage: null, Spec: spec);

    // ── the snapshot survives, byte for byte ────────────────────────────────

    public static TheoryData<string> Stores => new() { "json", "sqlite" };

    private IRunStore StoreOf(string kind)
        => kind == "json" ? new JsonRunStore(_workspace) : new SqliteRunStore(_workspace);

    /// <summary>
    /// Verbatim, not "equivalent". The point of a canonical snapshot is that two runs can be
    /// compared by comparing their specifications; a store that re-serialized it on the way through
    /// would make two identical runs look different.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_stored_run_keeps_its_specification_exactly(string kind)
    {
        var snapshot = Spec().Snapshot();
        var store = StoreOf(kind);

        await store.SaveAsync(Record(snapshot), CancellationToken.None);
        var back = Assert.Single(await store.LoadAllAsync(CancellationToken.None));

        Assert.Equal(snapshot, back.Spec);
    }

    /// <summary>Most runs are typed, not started from a template, and must stay perfectly ordinary.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_run_that_came_from_no_template_has_no_specification(string kind)
    {
        var store = StoreOf(kind);

        await store.SaveAsync(Record(null), CancellationToken.None);
        var back = Assert.Single(await store.LoadAllAsync(CancellationToken.None));

        Assert.Null(back.Spec);
    }

    /// <summary>
    /// The decisive one for the schema. Every installation that has run once already has a runs
    /// table without this column, and a store that only worked on a fresh database would lose
    /// everybody's history the first time they updated.
    /// </summary>
    [Fact]
    public async Task A_database_created_before_the_column_existed_is_upgraded_in_place()
    {
        var path = Path.Combine(_root, ".enactive", "enactive.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var oldRunId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();

            // The schema exactly as it was before spec_json: nothing here mentions it.
            using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE runs (
                  run_id TEXT PRIMARY KEY, task_id TEXT, title TEXT, model TEXT,
                  started_at TEXT, finished_at TEXT, status TEXT,
                  events_json TEXT, artifacts_json TEXT, decisions_json TEXT,
                  settings_json TEXT, usage_json TEXT
                );
                """;
            await create.ExecuteNonQueryAsync();

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO runs (run_id, task_id, title, model, started_at, finished_at, status,
                                  events_json, artifacts_json, decisions_json)
                VALUES ($id, $id, 'an older run', 'old-model', $at, $at, 'Completed', '[]', '[]', '[]');
                """;
            insert.Parameters.AddWithValue("$id", oldRunId.ToString());
            insert.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
            await insert.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var store = new SqliteRunStore(_workspace);

        // The old row is still readable, and simply has no specification.
        var older = Assert.Single(await store.LoadAllAsync(CancellationToken.None));
        Assert.Equal(oldRunId, older.RunId);
        Assert.Equal("an older run", older.Title);
        Assert.Null(older.Spec);

        // And the upgraded table takes one.
        var snapshot = Spec().Snapshot();
        await store.SaveAsync(Record(snapshot), CancellationToken.None);

        var all = await store.LoadAllAsync(CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Equal(snapshot, all.Single(r => r.RunId != oldRunId).Spec);
        Assert.Null(all.Single(r => r.RunId == oldRunId).Spec);
    }

    // ── reading one back ────────────────────────────────────────────────────

    [Fact]
    public void A_snapshot_parses_back_into_values()
    {
        var parsed = ResolvedTaskSpec.Parse(Spec().Snapshot());

        Assert.NotNull(parsed);
        Assert.Equal("release-check", parsed!.TemplateId);
        Assert.Equal(4, parsed.TemplateVersion);
        Assert.Equal("Build Enactive.sln and report.", parsed.Goal);
        Assert.Equal("Enactive.sln", parsed.Parameters["solution"]);
        Assert.Equal("developer", parsed.WorkerId);
        Assert.Equal(12, parsed.Limits.MaxSteps);
        Assert.Equal("Builds", Assert.Single(parsed.SuccessCriteria).Name);

        // The autonomy tier travels by NAME, so a value inserted into the middle of the enum later
        // cannot silently re-interpret an old run as a more permissive one.
        Assert.Equal(PermissionLevel.Execute, parsed.Permissions.Level);
        Assert.Equal(new[] { "git_push" }, parsed.Permissions.Deny);
        Assert.Equal(new[] { "run_command" }, parsed.Permissions.AskBefore);
    }

    /// <summary>
    /// A history view must open a run it cannot fully understand rather than refuse to. Null, empty,
    /// a truncated column and a shape from another version all mean the same thing here: no
    /// specification to show.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"TemplateId\":\"half-a-r")]
    [InlineData("not json at all")]
    public void An_unreadable_snapshot_is_no_specification_rather_than_a_crash(string? snapshot)
        => Assert.Null(ResolvedTaskSpec.Parse(snapshot));
}
