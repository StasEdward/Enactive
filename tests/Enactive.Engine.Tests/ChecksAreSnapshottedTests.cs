namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Workers;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// Plan Phase 3.1: a run's declared checks are read ONCE, when it is composed, and carried whole - in its resources,
/// its checkpoint and its record. Editing or deleting a manifest afterwards changes neither the run nor its resume.
/// </summary>
public sealed class ChecksAreSnapshottedTests
{
    private const string TwoStepPlan = """
        {"disposition":"task","title":"two steps",
         "steps":[{"title":"first","dependsOn":[]},{"title":"second","dependsOn":[0]}]}
        """;

    private const string Lint = """
        {"id":"lint","description":"Lints the sources","command":"npm run lint","matches":["src/**/*.ts"],
         "fireOn":"Step","severity":"Blocking","timeoutSeconds":45,"protects":["tests/**"]}
        """;

    private const string Audit = """{"id":"audit","command":"npm audit","fireOn":"Run"}""";

    private static void Manifest(EngineFixture fx, string name, string json) => fx.Write(Path.Combine(".enactive", "checks", name), json);

    private static string ManifestPath(EngineFixture fx, string name) => fx.PathOf(Path.Combine(".enactive", "checks", name));

    /// <summary>The checks the manifests above resolve to - written out, so the test does not compare the store with itself.</summary>
    private static IReadOnlyList<CheckDefinition> Expected() =>
    [
        new("audit", null, "npm audit", [], CheckFire.Run, CheckSeverity.Advisory, CheckLimits.DefaultTimeoutSeconds, null),
        new("lint", "Lints the sources", "npm run lint", ["src/**/*.ts"], CheckFire.Step, CheckSeverity.Blocking, 45, ["tests/**"])
    ];

    private static void AssertTheOriginalChecks(CheckSnapshot? snapshot)
    {
        Assert.NotNull(snapshot);
        Assert.Equal(Expected(), snapshot!.Checks.Select(c => c.Definition));
        Assert.All(snapshot.Checks, c => Assert.Equal(CheckOrigin.Workspace, c.Origin));
        Assert.Equal(["audit.json", "lint.json"], snapshot.Manifests.Select(m => m.File));
        Assert.All(snapshot.Manifests, m => Assert.Equal(64, m.Sha256.Length));
    }

    private static ComposedEngine Engine(FakeChatProvider provider, string globalChecks)
    {
        var models = new ModelResolver();
        var settings = new AppSettings { Engine = new() { ProposeChecks = false, ReviewRetries = 0, SuccessRetries = 0 } };
        var worker = EngineFixture.WorkerWith();
        return new ComposedEngine(new SingleProviderFactory(provider), new StaticWorkerProvider([worker], worker.Id),
            new ModelRouter(models), models, new ToolRegistry(EngineFixture.ShippedTools()), [], settings, new LogHub(),
            new SessionApprovals())
        {
            Planner = new Planner(checksAuditEnabled: false),
            Approvals = new ApprovalStore(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-permissions.json")),
            GlobalChecks = globalChecks
        };
    }

    private static async Task<List<WorkEvent>> EventsOf(ComposedRun composed)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<WorkEvent>();
        await foreach (var ev in composed.Events(cts.Token))
            events.Add(ev);
        return events;
    }

    /// <summary>
    /// THE ONE THAT MATTERS. Composed, the run holds the full definitions; the manifests are then edited and deleted, and
    /// the run, its checkpoint, its record - and the resumed run and ITS record - all still carry what was composed.
    /// </summary>
    [Fact]
    public async Task A_composed_run_and_its_resume_keep_the_checks_they_were_composed_with()
    {
        using var fx = new EngineFixture();
        Manifest(fx, "lint.json", Lint);
        Manifest(fx, "audit.json", Audit);
        var engine = Engine(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") }, fx.Root + "-no-global");
        var pause = new RunPause();
        pause.Request();

        List<WorkEvent> paused;
        await using (var composed = await RunComposer.ComposeAsync(engine,
                         new RunRequest(fx.Workspace, "write the report", IntentSource.CommandBar) { Defaults = new(2), Pause = pause },
                         new ScriptedDecisionHandler("allow"), CancellationToken.None))
        {
            AssertTheOriginalChecks(composed.Checks);

            // After compose, before the run reads anything: a person edits one manifest and deletes the other.
            Manifest(fx, "lint.json", Lint.Replace("npm run lint", "true"));
            File.Delete(ManifestPath(fx, "audit.json"));

            paused = await EventsOf(composed);
        }
        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());

        // The checkpoint on disk, through JSON, carries the definitions whole.
        var kept = Assert.Single(await new JsonCheckpointStore(fx.Workspace).LoadAllAsync(CancellationToken.None));
        AssertTheOriginalChecks(kept.CheckSnapshot);

        // A person deletes the last manifest too, then resumes.
        File.Delete(ManifestPath(fx, "lint.json"));
        List<WorkEvent> resumed;
        await using (var composed = await RunComposer.ComposeAsync(engine,
                         new RunRequest(fx.Workspace, kept.Request, IntentSource.CommandBar, Resume: kept) { Defaults = new(2) },
                         new ScriptedDecisionHandler("allow"), CancellationToken.None))
        {
            AssertTheOriginalChecks(composed.Checks);
            resumed = await EventsOf(composed);
        }
        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());

        // Both records - the paused run's and the resumed run's - say what judged them.
        var runs = RunStoreFactory.Create(fx.Workspace);
        var headers = await runs.LoadSummariesAsync(CancellationToken.None);
        Assert.Equal(2, headers.Count);
        foreach (var header in headers)
            AssertTheOriginalChecks((await runs.LoadAsync(header.RunId, CancellationToken.None))!.CheckSnapshot);

        // The people's edits stand: nothing the run did restored a manifest they removed.
        Assert.False(File.Exists(ManifestPath(fx, "lint.json")));
        Assert.False(File.Exists(ManifestPath(fx, "audit.json")));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>A checkpoint store that keeps what it was given, in memory.</summary>
    private sealed class Kept : IRunCheckpointStore
    {
        private readonly Dictionary<Guid, RunCheckpoint> _kept = new();
        public List<RunCheckpoint> Saved { get; } = [];

        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct)
        {
            lock (_kept) { _kept[checkpoint.RunId] = checkpoint; Saved.Add(checkpoint); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct)
        {
            lock (_kept) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(_kept.Values.ToArray());
        }

        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) return Task.FromResult(_kept.GetValueOrDefault(runId));
        }

        public Task DeleteAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) _kept.Remove(runId);
            return Task.CompletedTask;
        }
    }

    private static CheckSnapshot SnapshotOf(params CheckDefinition[] checks)
        => new(checks.Select(c => new ResolvedCheck(c, CheckOrigin.Workspace)).ToArray(), []);

    /// <summary>
    /// The engine, not only the composer: a resumed run is judged by its checkpoint's checks even when the orchestrator
    /// it is resumed on was handed others - every checkpoint the resumed run writes carries the original.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_writes_its_checkpoints_with_the_checks_it_was_started_with()
    {
        var started = SnapshotOf(new CheckDefinition("lint", null, "npm run lint", ["src/**"], CheckFire.Step, CheckSeverity.Blocking, 30, null));
        var edited = SnapshotOf(new CheckDefinition("lint", null, "true", ["src/**"], CheckFire.Step, CheckSeverity.Advisory, 30, null));
        using var fx = new EngineFixture { ProposeChecks = false, Checks = started, Pause = new RunPause() };
        fx.Pause.Request();
        var store = new Kept();

        var paused = await fx.RunAsync(fx.Build(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") },
            checkpoints: store), "do two things");
        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());
        var checkpoint = Assert.Single(await store.LoadAllAsync(default));
        Assert.Same(started, checkpoint.CheckSnapshot);

        fx.Pause = null;
        fx.Checks = edited;
        store.Saved.Clear();
        var resumed = await fx.ResumeAsync(fx.Build(new FakeChatProvider { WhenExhausted = Turn.Says("step done") }, checkpoints: store), checkpoint);

        Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
        Assert.NotEmpty(store.Saved);
        Assert.All(store.Saved, c => Assert.Same(started, c.CheckSnapshot));
    }

    [Fact]
    public async Task A_run_with_no_checks_writes_checkpoints_without_them()
    {
        using var fx = new EngineFixture { ProposeChecks = false, Pause = new RunPause() };
        fx.Pause.Request();
        var store = new Kept();

        await fx.RunAsync(fx.Build(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") },
            checkpoints: store), "do two things");

        var checkpoint = Assert.Single(await store.LoadAllAsync(default));
        Assert.Null(checkpoint.CheckSnapshot);
        Assert.Null(checkpoint.PendingChecks);
    }

    // ── what is already on disk still loads ─────────────────────────────────

    [Fact]
    public async Task A_checkpoint_written_before_check_manifests_still_loads_and_one_written_now_round_trips()
    {
        using var fx = new EngineFixture();
        var store = new JsonCheckpointStore(fx.Workspace);
        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, "ask", "title",
            null, null, [new CheckpointStep(Guid.NewGuid(), "first", [], "Normal", "Pending")], [], [], [], 0, 0)
        {
            CheckSnapshot = SnapshotOf(Expected().ToArray()) with { NoImplicitChecks = "none here" },
            PendingChecks = [new PendingCheck("lint", CheckBoundary.Wave, "the wave has not drained") { Wave = 2 }]
        };
        await store.SaveAsync(checkpoint, default);

        var back = (await store.LoadAsync(checkpoint.RunId, default))!;
        Assert.Equal(Expected(), back.CheckSnapshot!.Checks.Select(c => c.Definition));
        Assert.Equal("none here", back.CheckSnapshot.NoImplicitChecks);
        var pending = Assert.Single(back.PendingChecks!);
        Assert.Equal(CheckBoundary.Wave, pending.DeferredTo);
        Assert.Equal(2, pending.Wave);

        // The same file as a build before this one wrote it: neither member at all.
        var file = Directory.GetFiles(fx.PathOf(Path.Combine(".enactive", "checkpoints")), "*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(json.Remove("CheckSnapshot"));
        Assert.True(json.Remove("PendingChecks"));
        File.WriteAllText(file, json.ToJsonString());

        var old = (await store.LoadAsync(checkpoint.RunId, default))!;
        Assert.Null(old.CheckSnapshot);
        Assert.Null(old.PendingChecks);
        Assert.Equal("first", Assert.Single(old.Steps).Title);
    }

    private static RunRecord Record() => new(
        Guid.NewGuid(), Guid.NewGuid(), "a run", "fake-model", DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now,
        "Completed", [], [], []);

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public async Task A_run_record_keeps_its_checks_and_one_without_them_has_none(string kind)
    {
        using var fx = new EngineFixture();
        IRunStore store = kind == "json" ? new JsonRunStore(fx.Workspace) : new SqliteRunStore(fx.Workspace);
        var with = Record() with { CheckSnapshot = SnapshotOf(Expected().ToArray()) };
        var without = Record();

        await store.SaveAsync(with, default);
        await store.SaveAsync(without, default);

        Assert.Equal(Expected(), (await store.LoadAsync(with.RunId, default))!.CheckSnapshot!.Checks.Select(c => c.Definition));
        Assert.Null((await store.LoadAsync(without.RunId, default))!.CheckSnapshot);
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    [Fact]
    public async Task A_run_record_file_written_before_check_manifests_still_loads()
    {
        using var fx = new EngineFixture();
        var store = new JsonRunStore(fx.Workspace);
        var record = Record();
        await store.SaveAsync(record, default);
        var file = Directory.GetFiles(fx.PathOf(Path.Combine(".enactive", "runs")), "*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(json.Remove("CheckSnapshot"));
        File.WriteAllText(file, json.ToJsonString());

        var back = Assert.Single(await store.LoadAllAsync(default));

        Assert.Equal(record.RunId, back.RunId);
        Assert.Null(back.CheckSnapshot);
    }

    /// <summary>Every installation already has a runs table without the column; it is added in place and old rows load.</summary>
    [Fact]
    public async Task A_database_created_before_the_checks_column_is_upgraded_in_place()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf(Path.Combine(".enactive", "enactive.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var oldRunId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            using var create = connection.CreateCommand();
            // The schema exactly as it was before checks_json.
            create.CommandText =
                """
                CREATE TABLE runs (
                  run_id TEXT PRIMARY KEY, task_id TEXT, title TEXT, model TEXT,
                  started_at TEXT, finished_at TEXT, status TEXT,
                  events_json TEXT, artifacts_json TEXT, decisions_json TEXT,
                  settings_json TEXT, usage_json TEXT, spec_json TEXT
                );
                INSERT INTO runs (run_id, task_id, title, model, started_at, finished_at, status,
                                  events_json, artifacts_json, decisions_json)
                VALUES ($id, $id, 'an older run', 'old-model', $at, $at, 'Completed', '[]', '[]', '[]');
                """;
            create.Parameters.AddWithValue("$id", oldRunId.ToString());
            create.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
            await create.ExecuteNonQueryAsync();
        }
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);

        var store = new SqliteRunStore(fx.Workspace);
        var older = Assert.Single(await store.LoadAllAsync(default));
        Assert.Null(older.CheckSnapshot);

        var newer = Record() with { CheckSnapshot = SnapshotOf(Expected().ToArray()) };
        await store.SaveAsync(newer, default);
        Assert.Equal(Expected(), (await store.LoadAsync(newer.RunId, default))!.CheckSnapshot!.Checks.Select(c => c.Definition));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }
}
