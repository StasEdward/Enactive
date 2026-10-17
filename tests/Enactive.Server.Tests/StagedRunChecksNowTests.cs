namespace Enactive.Server.Tests;

using System.Security.Claims;
using System.Text;
using Enactive.Application;
using Enactive.Core.Artifacts;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Settings;
using Enactive.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// "Run checks now" on the service (Plan Phase 3.2, staged runs): commands run in a workspace from a page, so only by the
/// roles that may apply proposals (ServerStaging.MayDecide), each press in the audit trail next to apply and reject - and
/// a refused press too. The record is the run's, listed on its history for anybody who reads it.
/// </summary>
public sealed class StagedRunChecksNowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-checks-now", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly UserStore _users;
    private readonly ServerEngine _engine;
    private readonly ServerHistory _history;

    public StagedRunChecksNowTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        // An engine to run on: a provider and a worker named, never called - running checks asks no model.
        new AppSettings
        {
            Providers = [new ProviderConfig { Id = "fixture", BaseUrl = "http://localhost:1", Models = ["fixture"] }],
            Workers = [new WorkerConfig { Id = "fixture", Role = "fixture", Model = "fixture/fixture" }]
        }.Save(Path.Combine(data, "settings.json"), replaceUnreadable: true);
        _users = new UserStore(data);
        _users.Add("alice", "correct horse battery", Role.Operator);
        _users.Add("vera", "correct horse battery", Role.Viewer);
        _engine = new ServerEngine(data, new FileLogSink(Path.Combine(_root, "logs")));
        _engine.Workspaces.Touch(_workspace);
        var control = new RunControl(_users, new RunQueue(maxActive: 1), new Idle(_engine.Approvals), NullLogger<RunControl>.Instance);
        _history = new ServerHistory(_users, _engine, control, new KnownWorkspaces(_engine, new WorkspaceRoots([_root])));
    }

    public void Dispose()
    {
        _engine.Dispose();
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private sealed class Idle(ApprovalStore approvals) : IRunExecutor
    {
        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;
        public Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct) => Task.CompletedTask;
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    /// <summary>A staged run's record, judged by one check - "notes", which echoes - as the service records it when the run ends.</summary>
    private async Task<RunRecord> StagedRunAsync()
    {
        var checks = Path.Combine(_workspace, ".enactive", "checks");
        Directory.CreateDirectory(checks);
        File.WriteAllText(Path.Combine(checks, "notes.json"), """{"id":"notes","command":"echo ran","fireOn":"Run"}""");
        File.WriteAllText(Path.Combine(_workspace, "notes.md"), "applied\n");
        var snapshot = new CheckStore(_workspace, Path.Combine(_root, "no-global"), KnownEcosystems.All).Snapshot();
        var at = DateTimeOffset.Now.AddMinutes(-5);
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "write the notes", "fixture", at, at.AddSeconds(5), "Incomplete",
            [], ["notes.md"], [], new RunSettings(3, "autonomous", null, Staged: true)) { CheckSnapshot = snapshot };
        await RunStoreFactory.Create(WorkspaceInfo.For(_workspace)).SaveAsync(run, CancellationToken.None);
        return run;
    }

    [Fact]
    public async Task A_role_that_may_not_apply_proposals_is_refused_and_the_refusal_is_audited()
    {
        var run = await StagedRunAsync();

        var said = await _history.RunChecksNowAsync(As("vera"), _workspace, run.RunId, CancellationToken.None);

        Assert.Contains("role does not include", said);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "vera", Action: "staged run checks refused" });
        Assert.Empty(await RunStoreFactory.Create(WorkspaceInfo.For(_workspace)).LoadCheckRecordsAsync(run.RunId, CancellationToken.None));
        // Offered on the page - the run is staged and has checks - but not to this person.
        var (records, offered, canRun, _) = await _history.ChecksAsync(As("vera"), _workspace, run.RunId, CancellationToken.None);
        Assert.Empty(records);
        Assert.True(offered);
        Assert.False(canRun);
    }

    [Fact]
    public async Task An_operators_press_runs_the_checks_is_audited_and_is_listed_for_everybody()
    {
        var run = await StagedRunAsync();
        // The workspace's level now - the permissions the checks run under - lets commands run.
        _engine.Workspaces.SaveSettings(_workspace, 3, null, stageChanges: true);

        var said = await _history.RunChecksNowAsync(As("alice"), _workspace, run.RunId, CancellationToken.None);

        Assert.StartsWith("Checked: 1 passed", said);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "staged run checked now" });
        var (records, offered, canRun, problem) = await _history.ChecksAsync(As("vera"), _workspace, run.RunId, CancellationToken.None);
        Assert.Null(problem);
        Assert.True(offered);
        Assert.False(canRun);
        var record = Assert.Single(records);
        Assert.StartsWith("Checked at ", record.Headline);
        Assert.Contains("by alice", record.Headline);
        Assert.False(record.Outdated);
        Assert.StartsWith("PASS notes", Assert.Single(record.Lines).Text);
        Assert.True((await _history.ChecksAsync(As("alice"), _workspace, run.RunId, CancellationToken.None)).CanRun);

        // The workspace changes: the record says it is outdated, and is still listed.
        File.WriteAllText(Path.Combine(_workspace, "notes.md"), "edited since\n");
        Assert.True(Assert.Single((await _history.ChecksAsync(As("vera"), _workspace, run.RunId, CancellationToken.None)).Records).Outdated);
        // And the run is as it ended.
        Assert.Equal("Incomplete", (await RunStoreFactory.Create(WorkspaceInfo.For(_workspace)).LoadAsync(run.RunId, CancellationToken.None))!.Status);
    }

    /// <summary>A run that was not staged is not offered the action, and a press is refused - it had its checks as it ran.</summary>
    [Fact]
    public async Task A_run_that_was_not_staged_is_not_offered_it()
    {
        var at = DateTimeOffset.Now;
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "t", null, at, at, "Completed", [], [], [], new RunSettings(3, "autonomous", null, Staged: false));
        await RunStoreFactory.Create(WorkspaceInfo.For(_workspace)).SaveAsync(run, CancellationToken.None);

        Assert.False((await _history.ChecksAsync(As("alice"), _workspace, run.RunId, CancellationToken.None)).Offered);
        Assert.Contains("staged", await _history.RunChecksNowAsync(As("alice"), _workspace, run.RunId, CancellationToken.None));
    }

    /// <summary>
    /// Its proposals' page offers it once the run has ended: the set says which run in the history it is - its folder is
    /// named for the service's run, not the history's.
    /// </summary>
    [Fact]
    public async Task A_set_says_which_run_of_the_history_it_is_once_the_run_has_ended()
    {
        var staging = new ServerStaging(_users, new KnownWorkspaces(_engine, new WorkspaceRoots([_root])));
        var folder = staging.Begin(_workspace, Guid.NewGuid(), "alice", "tidy the notes");
        var store = new StagingArtifactStore(_workspace, folder);
        await store.CreateAsync("notes.md", ArtifactKind.FileSet, "notes", s => s.WriteAsync(Encoding.UTF8.GetBytes("new\n")).AsTask(), CancellationToken.None);
        Assert.Null(Assert.Single(staging.List(As("vera"))).RunId);
        var runId = Guid.NewGuid();

        await staging.EndAsync(_workspace, folder, "tidy the notes", runId, store);
        store.Dispose();

        Assert.Equal(runId, Assert.Single(staging.List(As("vera"))).RunId);
    }
}
