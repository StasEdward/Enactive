namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Agents;
using Enactive.Core.Checks;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Workers;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// Plan Phase 3.2, staged runs: "Run checks now". After a person has decided a staged run's proposals - and the set's
/// folder is gone, as the service deletes it - the run's SNAPSHOTTED checks run under the permissions of the moment against
/// the workspace as it is now, and the result is a check record in the run's history, with a fingerprint of the state that
/// marks it outdated once the workspace changes. The run's own outcome is never touched.
/// </summary>
public sealed class RunChecksNowTests
{
    private const string QuickAnswer = """{"disposition":"quick_action","title":"notes","steps":[]}""";

    private static void Manifest(EngineFixture fx, string name, string json) => fx.Write(Path.Combine(".enactive", "checks", name), json);

    /// <summary>An engine as EngineComposition.Build makes one, with a scripted model that writes <paramref name="writes"/>.</summary>
    private static ComposedEngine Engine(params (string Path, string Content)[] writes)
    {
        var turns = new List<Turn> { Turn.Says(QuickAnswer) };
        var call = 0;
        foreach (var (path, content) in writes)
            turns.Add(Turn.Calls1("write_file", System.Text.Json.JsonSerializer.Serialize(new { path, content }), $"call_{++call}"));
        turns.Add(Turn.Says("done"));
        var models = new ModelResolver();
        var settings = new AppSettings { Engine = new() { ProposeChecks = false, ReviewRetries = 0, SuccessRetries = 0 } };
        var worker = EngineFixture.WorkerWith("write_file");
        return new ComposedEngine(new SingleProviderFactory(new FakeChatProvider([.. turns])), new StaticWorkerProvider([worker], worker.Id),
            new ModelRouter(models), models, new ToolRegistry(EngineFixture.ShippedTools()), [], settings, new LogHub(),
            new SessionApprovals())
        {
            Planner = new Planner(checksAuditEnabled: false),
            Approvals = new ApprovalStore(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-permissions.json")),
            GlobalChecks = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-global-checks")
        };
    }

    /// <summary>
    /// A staged run that proposed <paramref name="writes"/>, ended - its proposals kept on disk, as the service keeps them
    /// (KeepProposalsIn). Returns the run's id and the folder the proposals are in.
    /// </summary>
    private static async Task<(Guid RunId, string Kept, ComposedEngine Engine)> StagedRunAsync(EngineFixture fx, params (string Path, string Content)[] writes)
    {
        var engine = Engine(writes);
        var kept = Path.Combine(fx.Root, ".enactive", "staging", Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<WorkEvent>();
        await using (var composed = await RunComposer.ComposeAsync(engine,
                         new RunRequest(fx.Workspace, "write the notes", IntentSource.CommandBar, Stage: true)
                             { Defaults = new(AutonomyTiers.Highest), KeepProposalsIn = kept },
                         fx.Decisions, cts.Token))
        {
            Assert.IsType<StagingArtifactStore>(composed.Artifacts);
            await foreach (var ev in composed.Events(cts.Token))
                events.Add(ev);
            ((StagingArtifactStore)composed.Artifacts).Dispose();
        }
        foreach (var (path, _) in writes)
            Assert.False(fx.Exists(path), $"{path} is a proposal, not on disk, until it is applied.");
        return (events[0].RunId, kept, engine);
    }

    /// <summary>Applies the proposals for <paramref name="paths"/>, as a person does one file at a time; deletes the set once all are decided.</summary>
    private static void Apply(EngineFixture fx, string kept, params string[] paths)
    {
        bool decided;
        using (var store = StagingArtifactStore.Reopen(fx.Root, kept))
        {
            foreach (var change in store.Changes.Where(c => paths.Contains(c.RelativePath, StringComparer.OrdinalIgnoreCase)))
                Assert.True(store.Apply(change.Id).Applied);
            decided = store.Changes.All(c => !c.Pending);
        }
        // As ServerStaging does once every proposal is decided: the set's folder is gone.
        if (decided)
            Directory.Delete(kept, recursive: true);
    }

    private static Task<ChecksNowResult> RunNowAsync(ComposedEngine engine, EngineFixture fx, Guid runId, int autonomy = 3)
        => ChecksNow.RunAsync(engine, fx.Workspace, runId, "alice", autonomy, CancellationToken.None);

    private static void Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private static void Repository(EngineFixture fx)
    {
        Git(fx.Root, "init", "-q");
        Git(fx.Root, "config", "user.email", "t@example.com");
        Git(fx.Root, "config", "user.name", "t");
        Git(fx.Root, "config", "core.autocrlf", "false");
        Git(fx.Root, "add", "-A");
        Git(fx.Root, "commit", "-q", "-m", "start");
    }

    /// <summary>
    /// THE ONE THAT MATTERS. Every proposal decided, the set's folder gone: Run checks now keeps a record in the run's
    /// history - who, when, the definitions, the results - read back from the store; the run's outcome and events are as
    /// they were; and what ran is the check as the run snapshotted it, not as its manifest says since.
    /// </summary>
    [Fact]
    public async Task Checks_run_now_after_every_proposal_is_decided_are_kept_in_the_runs_history()
    {
        using var fx = new EngineFixture();
        Manifest(fx, "notes.json", """{"id":"notes","command":"echo snapshotted-check-ran","fireOn":"Run","severity":"Blocking"}""");
        var (runId, kept, engine) = await StagedRunAsync(fx, ("notes.md", "applied\n"));
        // The manifest edited since the run: the run is judged by what it was started with, and so are its checks now.
        Manifest(fx, "notes.json", """{"id":"notes","command":"exit 1","fireOn":"Run","severity":"Blocking"}""");
        Apply(fx, kept, "notes.md");
        Assert.False(Directory.Exists(kept));

        var store = RunStoreFactory.Create(fx.Workspace);
        var before = (await store.LoadAsync(runId, CancellationToken.None))!;
        Assert.True(before.Settings!.Staged);

        var (record, problem) = await RunNowAsync(engine, fx, runId);

        Assert.Null(problem);
        Assert.NotNull(record);
        var kept1 = Assert.Single(await RunStoreFactory.Create(fx.Workspace).LoadCheckRecordsAsync(runId, CancellationToken.None));
        Assert.Equal(record!.Id, kept1.Id);
        Assert.Equal((runId, "alice"), (kept1.RunId, kept1.Who));
        Assert.Equal("echo snapshotted-check-ran", Assert.Single(kept1.Checks, c => c.Id == "notes").Command);
        var line = Assert.Single(kept1.Results, r => r.CheckId == "notes");
        Assert.Equal(CriterionOutcome.Passed, line.Outcome);
        Assert.Equal(0, line.ExitCode);
        // Its whole output in a detail file of its own, named for this record.
        Assert.NotNull(line.DetailPath);
        Assert.StartsWith(CheckRecords.RelativeRunFolder(runId) + "/now-", line.DetailPath);
        Assert.Contains("snapshotted-check-ran", File.ReadAllText(fx.PathOf(line.DetailPath!)));

        // The run is what it was: its outcome and its record are not rewritten by a check run after it.
        var after = (await store.LoadAsync(runId, CancellationToken.None))!;
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Events.Count, after.Events.Count);
        Assert.Equal(before.FinishedAt, after.FinishedAt);

        // Another press is another record, beside the first.
        var (second, _) = await RunNowAsync(engine, fx, runId);
        Assert.Equal([record.Id, second!.Id], (await store.LoadCheckRecordsAsync(runId, CancellationToken.None)).Select(r => r.Id));
        Assert.NotEqual(line.DetailPath, Assert.Single(second.Results, r => r.CheckId == "notes").DetailPath);

        // Deleting the run from the history takes its records with it.
        await store.DeleteAsync(runId, CancellationToken.None);
        Assert.Empty(await store.LoadCheckRecordsAsync(runId, CancellationToken.None));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>A check the current policy would ask about is not run and not put to anybody: "not run: needs permission".</summary>
    [Fact]
    public async Task A_check_the_current_policy_would_ask_about_is_recorded_as_not_run_needs_permission()
    {
        using var fx = new EngineFixture();
        Manifest(fx, "notes.json", """{"id":"notes","command":"echo ran","fireOn":"Run"}""");
        var (runId, kept, engine) = await StagedRunAsync(fx, ("notes.md", "applied\n"));
        Apply(fx, kept, "notes.md");

        // "execute": the run itself was autonomous; the level of the moment asks before a command.
        var (record, problem) = await RunNowAsync(engine, fx, runId, autonomy: 2);

        Assert.Null(problem);
        var line = Assert.Single(record!.Results, r => r.CheckId == "notes");
        Assert.Equal(CriterionOutcome.Unknown, line.Outcome);
        Assert.StartsWith("not run: needs permission", line.Detail);
        Assert.Null(line.ExitCode);
        Assert.Empty(fx.Decisions.Requests);
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>
    /// In git: a record is current while nothing changed, and outdated once another proposal is applied or a tracked file
    /// is edited - read by recomputing the fingerprint and comparing it.
    /// </summary>
    [Fact]
    public async Task In_git_applying_another_file_or_editing_a_tracked_one_marks_the_record_outdated()
    {
        using var fx = new EngineFixture();
        fx.Write("readme.md", "start\n");
        Manifest(fx, "notes.json", """{"id":"notes","command":"echo ran","fireOn":"Run"}""");
        Repository(fx);
        var (runId, kept, engine) = await StagedRunAsync(fx, ("notes.md", "one\n"), ("more.md", "two\n"));
        Apply(fx, kept, "notes.md");

        var (record, _) = await RunNowAsync(engine, fx, runId);
        Assert.Equal(FingerprintKind.Git, record!.Fingerprint.Kind);
        Assert.NotNull(record.Fingerprint.Head);
        Assert.True(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None), "Nothing changed since the checks ran.");

        Apply(fx, kept, "more.md");
        Assert.False(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None), "Another proposal was applied since.");

        var (again, _) = await RunNowAsync(engine, fx, runId);
        Assert.True(await ChecksNow.IsCurrentAsync(fx.Root, again!, CancellationToken.None));
        fx.Write("readme.md", "edited\n");
        Assert.False(await ChecksNow.IsCurrentAsync(fx.Root, again!, CancellationToken.None), "A tracked file was edited since.");
        // The first record stays outdated: it is about a state that is gone.
        Assert.False(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>
    /// Outside git: the fingerprint is the content of the paths the run proposed and of the files the checks' matches cover -
    /// and says it does not catch everything, because it does not: a file outside both changes unseen.
    /// </summary>
    [Fact]
    public async Task Outside_git_the_fingerprint_is_the_proposed_and_matched_paths_and_says_it_does_not_catch_everything()
    {
        using var fx = new EngineFixture();
        fx.Write("src/a.txt", "a\n");
        fx.Write("other.txt", "elsewhere\n");
        Manifest(fx, "lint.json", """{"id":"lint","command":"echo ran","matches":["src/**"],"fireOn":"Step"}""");
        var (runId, kept, engine) = await StagedRunAsync(fx, ("notes.md", "applied\n"));
        Apply(fx, kept, "notes.md");

        var (record, _) = await RunNowAsync(engine, fx, runId);
        var fingerprint = record!.Fingerprint;
        Assert.Equal(FingerprintKind.Files, fingerprint.Kind);
        Assert.Equal(["notes.md"], fingerprint.Proposed);
        Assert.Equal(["src/**"], fingerprint.Matches);
        Assert.Equal(2, fingerprint.Files);
        Assert.Contains("does not catch everything", fingerprint.Caveat);
        Assert.True(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None));

        // Outside both: not caught - which is what the caveat says.
        fx.Write("other.txt", "changed\n");
        Assert.True(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None));

        fx.Write("src/a.txt", "changed\n");
        Assert.False(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None), "A file a check's matches cover changed.");
        fx.Write("src/a.txt", "a\n");
        Assert.True(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None));
        fx.Write("notes.md", "edited after\n");
        Assert.False(await ChecksNow.IsCurrentAsync(fx.Root, record, CancellationToken.None), "A file the run proposed changed.");
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>
    /// StateFingerprints.MaxFiles: a glob over more files than a fingerprint hashes takes the first of them and says the
    /// rest were not hashed - a record that looked current while an unhashed file changed would claim more than it holds.
    /// </summary>
    [Fact]
    public async Task A_fingerprint_past_the_most_files_it_hashes_says_so()
    {
        using var fx = new EngineFixture();
        Directory.CreateDirectory(fx.PathOf("src"));
        for (var i = 0; i <= StateFingerprints.MaxFiles; i++)
            File.WriteAllText(fx.PathOf($"src/f{i:D5}.txt"), "x");

        var fingerprint = await StateFingerprints.TakeAsync(fx.Root, [], ["src/**"], [], CancellationToken.None);

        Assert.Equal(FingerprintKind.Files, fingerprint.Kind);
        Assert.Equal(StateFingerprints.MaxFiles, fingerprint.Files);
        Assert.Contains($"1 past the first {StateFingerprints.MaxFiles} were not hashed", fingerprint.Caveat);
    }

    /// <summary>
    /// A workspace that is a FOLDER of a repository: git names changed files from the repository's top, and a fingerprint
    /// that hashed them as workspace paths found every one "(gone)" - a second edit to a file already changed went
    /// unnoticed, and the engine's own folder in the workspace was not recognised as its own.
    /// </summary>
    [Fact]
    public async Task In_a_repository_subfolder_a_second_edit_is_caught_and_the_engines_own_output_is_not()
    {
        using var fx = new EngineFixture();
        fx.Write("sub/b.txt", "start\n");
        fx.Write("top.txt", "top\n");
        Repository(fx);
        var root = fx.PathOf("sub");
        fx.Write("sub/b.txt", "first edit\n");

        var taken = await StateFingerprints.TakeAsync(root, [], [], [], CancellationToken.None);
        Assert.Equal(FingerprintKind.Git, taken.Kind);
        Assert.Equal(1, taken.Files);
        Assert.True(await StateFingerprints.MatchesAsync(root, taken, [], CancellationToken.None));

        // Written after a press, by the engine, under the workspace's own .enactive: not a change of the work.
        fx.Write("sub/.enactive/checks/runs/x/now-1-notes.txt", "output");
        Assert.True(await StateFingerprints.MatchesAsync(root, taken, [], CancellationToken.None));
        // Outside the workspace: another folder's work, not this one's.
        fx.Write("top.txt", "elsewhere\n");
        Assert.True(await StateFingerprints.MatchesAsync(root, taken, [], CancellationToken.None));

        fx.Write("sub/b.txt", "second edit\n");
        Assert.False(await StateFingerprints.MatchesAsync(root, taken, [], CancellationToken.None), "A second edit of a changed file.");
        fx.Write("sub/b.txt", "first edit\n");
        Assert.True(await StateFingerprints.MatchesAsync(root, taken, [], CancellationToken.None));
    }

    /// <summary>
    /// The run's own ceiling still holds: a template that denied the command tool for its run denies it here too - the
    /// permissions of now are narrowed by the template's, never wider than what the run was allowed.
    /// </summary>
    [Fact]
    public async Task A_command_the_runs_template_denied_is_not_run_now_either()
    {
        using var fx = new EngineFixture();
        Manifest(fx, "notes.json", """{"id":"notes","command":"echo ran","fireOn":"Run"}""");
        var spec = new ResolvedTaskSpec("tidy", 1, "Tidy", fx.Workspace.Id, fx.Workspace.Name, fx.Root, "tidy the notes",
            new Dictionary<string, string>(),
            Enactive.Core.Permissions.PermissionPolicy.PermissiveDefault with { Deny = ["run_command"] },
            [], ExecutionLimits.None, null, false);
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "Tidy", null, DateTimeOffset.Now, DateTimeOffset.Now, "Incomplete", [], [], [],
            new RunSettings(3, "autonomous", null, Staged: true), Spec: spec.Snapshot())
        {
            CheckSnapshot = new CheckStore(fx.Root, fx.Root + "-no-global", KnownEcosystems.All).Snapshot()
        };
        await RunStoreFactory.Create(fx.Workspace).SaveAsync(run, CancellationToken.None);

        var (record, _) = await RunNowAsync(Engine(), fx, run.RunId, autonomy: 3);

        var line = Assert.Single(record!.Results);
        Assert.Equal(CriterionOutcome.Unknown, line.Outcome);
        Assert.StartsWith("not run: needs permission", line.Detail);
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>
    /// One press at a time in a workspace, and never beside a run of this process: the workspace lease keeps out other
    /// processes only, so two people pressing at once on the service ran the same commands side by side, and a run started
    /// meanwhile edited the files being checked.
    /// </summary>
    [Fact]
    public async Task Checks_now_run_one_at_a_time_and_never_beside_a_run_of_this_process()
    {
        using var fx = new EngineFixture();
        Manifest(fx, "notes.json", """{"id":"notes","command":"echo ran","fireOn":"Run"}""");
        var (runId, kept, engine) = await StagedRunAsync(fx, ("notes.md", "applied\n"));
        Apply(fx, kept, "notes.md");

        using (Enactive.Core.Storage.WorkspaceActivity.TryChecks(fx.Root, out _)!)
        {
            var (none, busy) = await RunNowAsync(engine, fx, runId);
            Assert.Null(none);
            Assert.Contains("already running", busy);
            // And a run is not started in the middle of them.
            var refused = await Assert.ThrowsAsync<Enactive.Core.Storage.WorkspaceInUseException>(() => RunComposer.ComposeAsync(Engine(),
                new RunRequest(fx.Workspace, "another", IntentSource.CommandBar) { Defaults = new(2) }, fx.Decisions, CancellationToken.None));
            Assert.Contains("checks are running", refused.Message);
        }

        // A run composed in the workspace - going - keeps the checks out until it is over.
        await using (await RunComposer.ComposeAsync(Engine(), new RunRequest(fx.Workspace, "another", IntentSource.CommandBar) { Defaults = new(2) },
                         fx.Decisions, CancellationToken.None))
        {
            var (none, going) = await RunNowAsync(engine, fx, runId);
            Assert.Null(none);
            Assert.Contains("A run is going", going);
        }

        Assert.NotNull((await RunNowAsync(engine, fx, runId)).Record);
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>
    /// A check command that writes the checks folder - adds a manifest - is put back as a run's would be, and the record
    /// says so: a press of "Run checks now" may no more change the checks that judge the next run than a run may.
    /// </summary>
    [Fact]
    public async Task A_check_that_changes_the_manifests_is_put_back_and_the_record_says_so()
    {
        using var fx = new EngineFixture();
        Manifest(fx, "notes.json", """{"id":"notes","command":"echo planted > .enactive/checks/planted.json","fireOn":"Run"}""");
        var (runId, kept, engine) = await StagedRunAsync(fx, ("notes.md", "applied\n"));
        Apply(fx, kept, "notes.md");
        var manifest = File.ReadAllText(fx.PathOf(Path.Combine(".enactive", "checks", "notes.json")));

        var (record, problem) = await RunNowAsync(engine, fx, runId);

        Assert.Null(problem);
        Assert.Equal(CriterionOutcome.Passed, Assert.Single(record!.Results).Outcome);
        Assert.False(fx.Exists(Path.Combine(".enactive", "checks", "planted.json")));
        Assert.Equal(manifest, File.ReadAllText(fx.PathOf(Path.Combine(".enactive", "checks", "notes.json"))));
        Assert.Contains("added planted.json", record.Note, StringComparison.Ordinal);
        Assert.Contains("restored", record.Note, StringComparison.Ordinal);
        Assert.Contains("added planted.json", Assert.Single(await RunStoreFactory.Create(fx.Workspace).LoadCheckRecordsAsync(runId, CancellationToken.None)).Note,
            StringComparison.Ordinal);
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>Only a staged run is checked this way; a run that wrote to disk was checked as it ran.</summary>
    [Fact]
    public async Task A_run_that_was_not_staged_is_refused()
    {
        using var fx = new EngineFixture();
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "t", null, DateTimeOffset.Now, DateTimeOffset.Now, "Completed", [], [], [],
            new RunSettings(3, "autonomous", null, Staged: false));
        await RunStoreFactory.Create(fx.Workspace).SaveAsync(run, CancellationToken.None);

        var (record, problem) = await RunNowAsync(Engine(), fx, run.RunId);

        Assert.Null(record);
        Assert.Contains("staged", problem);
        Assert.Empty(await RunStoreFactory.Create(fx.Workspace).LoadCheckRecordsAsync(run.RunId, CancellationToken.None));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>A record through each store that ships, and back: by name for its enums, whole for its definitions.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public async Task A_check_record_is_kept_and_read_back_by_each_store(string kind)
    {
        using var fx = new EngineFixture();
        IRunStore store = kind == "json" ? new JsonRunStore(fx.Workspace) : new SqliteRunStore(fx.Workspace);
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "t", null, DateTimeOffset.Now, DateTimeOffset.Now, "Incomplete", [], [], [],
            new RunSettings(3, "autonomous", null, Staged: true));
        await store.SaveAsync(run, CancellationToken.None);
        var record = Record(run.RunId);

        await store.AddCheckRecordAsync(record, CancellationToken.None);

        var back = Assert.Single(await store.LoadCheckRecordsAsync(run.RunId, CancellationToken.None));
        Assert.Equal(record.Checks, back.Checks);
        Assert.Equal(record.Results, back.Results);
        Assert.Equal((record.Fingerprint.Kind, record.Fingerprint.Digest, record.Fingerprint.Caveat),
            (back.Fingerprint.Kind, back.Fingerprint.Digest, back.Fingerprint.Caveat));
        Assert.Equal(record.Fingerprint.Proposed, back.Fingerprint.Proposed);
        Assert.Equal((record.Who, record.At, record.Note), (back.Who, back.At, back.Note));
        Assert.Empty(await store.LoadCheckRecordsAsync(Guid.NewGuid(), CancellationToken.None));
        // The run's own record and its list are untouched by it.
        Assert.Single(await store.LoadSummariesAsync(CancellationToken.None));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    private static CheckRecord Record(Guid runId) => new(Guid.NewGuid(), runId, "alice", DateTimeOffset.Now,
        [new CheckDefinition("notes", null, "echo ran", [], CheckFire.Run, CheckSeverity.Blocking, 30, null)],
        [new CheckResultLine("notes", CriterionOutcome.Failed, true, "echo ran", 1, "it said no") { DetailPath = ".enactive/checks/runs/x/now-notes.txt" }],
        new StateFingerprint(FingerprintKind.Files, new string('a', 64)) { Proposed = ["notes.md"], Matches = [], Files = 1, Caveat = "does not catch everything" })
    {
        Note = "a note"
    };

    /// <summary>
    /// A database made before check records existed - only its runs table - loads as before, has no records for its runs,
    /// and keeps one from now on.
    /// </summary>
    [Fact]
    public async Task An_old_database_without_check_records_loads_and_keeps_them_from_now_on()
    {
        using var fx = new EngineFixture();
        var runId = Guid.NewGuid();
        var file = Path.Combine(fx.Root, ".enactive", "enactive.db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE runs (run_id TEXT PRIMARY KEY, task_id TEXT, title TEXT, model TEXT, started_at TEXT, finished_at TEXT,
                  status TEXT, events_json TEXT, artifacts_json TEXT, decisions_json TEXT);
                INSERT INTO runs VALUES ($id, $task, 'old run', NULL, '2026-01-01T00:00:00.0000000+00:00',
                  '2026-01-01T00:01:00.0000000+00:00', 'Completed', '[]', '[]', '[]');
                """;
            command.Parameters.AddWithValue("$id", runId.ToString());
            command.Parameters.AddWithValue("$task", Guid.NewGuid().ToString());
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        IRunStore store = new SqliteRunStore(fx.Workspace);
        Assert.Equal("old run", (await store.LoadAsync(runId, CancellationToken.None))!.Title);
        Assert.Empty(await store.LoadCheckRecordsAsync(runId, CancellationToken.None));

        await store.AddCheckRecordAsync(Record(runId), CancellationToken.None);
        Assert.Single(await store.LoadCheckRecordsAsync(runId, CancellationToken.None));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>A file history written before check records: the run loads, and has none.</summary>
    [Fact]
    public async Task An_old_file_history_without_check_records_loads()
    {
        using var fx = new EngineFixture();
        IRunStore store = new JsonRunStore(fx.Workspace);
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "old", null, DateTimeOffset.Now, DateTimeOffset.Now, "Completed", [], [], []);
        await store.SaveAsync(run, CancellationToken.None);

        Assert.NotNull(await store.LoadAsync(run.RunId, CancellationToken.None));
        Assert.Empty(await store.LoadCheckRecordsAsync(run.RunId, CancellationToken.None));
        Assert.Single(await store.LoadAllAsync(CancellationToken.None));
    }
}
