namespace Enactive.Server.Tests;

using System.Security.Claims;
using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>
/// Persistent staging on the service (server plan A4): a browser run's proposals kept in its workspace, outliving the
/// page and the process, read by anybody signed in, applied or rejected by an Operator once the run has ended - each in
/// the audit trail - and gone when every one of them is decided.
/// </summary>
public sealed class ServerStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-staging", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly UserStore _users;
    private readonly ServerEngine _engine;
    private readonly ServerStaging _staging;

    public ServerStagingTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        File.WriteAllText(Path.Combine(_workspace, "notes.md"), "old\n");
        _users = new UserStore(_root);
        _users.Add("alice", "correct horse battery", Role.Operator);
        _users.Add("vera", "correct horse battery", Role.Viewer);
        _engine = new ServerEngine(_root, new FileLogSink(Path.Combine(_root, "logs")));
        _engine.Workspaces.Touch(_workspace);
        _staging = new ServerStaging(_users, _engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    private string Begin(ServerStaging staging) => staging.Begin(_workspace, Guid.NewGuid(), "alice", "tidy the notes");

    private Task EndAsync(ServerStaging staging, string folder, StagingArtifactStore store)
        => staging.EndAsync(_workspace, folder, "tidy the notes", Guid.NewGuid(), store);

    /// <summary>A run that proposed one change to notes.md, ended - as EngineExecutor does it.</summary>
    private async Task<string> StagedAsync(ServerStaging? staging = null, bool end = true)
    {
        staging ??= _staging;
        var folder = Begin(staging);
        var store = new StagingArtifactStore(_workspace, folder);
        await store.CreateAsync("notes.md", ArtifactKind.FileSet, "notes", s => s.WriteAsync(Encoding.UTF8.GetBytes("new\n")).AsTask(), CancellationToken.None);
        if (end)
            await EndAsync(staging, folder, store);
        store.Dispose();
        return folder;
    }

    [Fact]
    public async Task Proposals_are_listed_shown_and_applied_by_an_operator_and_audited()
    {
        var folder = await StagedAsync();
        var id = Path.GetFileName(folder);

        var set = Assert.Single(_staging.List(As("vera")));
        Assert.Equal((1, "alice"), (set.Pending, set.Owner));
        var (_, files, problem) = _staging.Open(As("vera"), _workspace, id);
        Assert.Null(problem);
        Assert.Contains("+ new", Assert.Single(files).Diff);

        Assert.Contains("role does not include", await _staging.ActAsync(As("vera"), _workspace, id, files[0].Id, apply: true, CancellationToken.None));
        Assert.StartsWith("Applied", await _staging.ActAsync(As("alice"), _workspace, id, files[0].Id, apply: true, CancellationToken.None));

        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_workspace, "notes.md")));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "staged change applied" });
        // Every proposal decided: nothing left to keep, nothing listed.
        Assert.False(Directory.Exists(folder));
        Assert.Empty(_staging.List(As("vera")));
    }

    /// <summary>The person who started it may have closed the page: the inbox says what waits (and is mailed).</summary>
    [Fact]
    public async Task A_run_ended_with_proposals_waiting_says_so_in_the_inbox()
    {
        await StagedAsync();

        var item = Assert.Single(await InboxStoreFactory.Create(WorkspaceInfo.For(_workspace)).LoadAllAsync(CancellationToken.None));
        Assert.Contains("1 proposed change(s) wait", item.Summary);
    }

    /// <summary>A restart of the service: the proposals are on disk, and another instance applies them.</summary>
    [Fact]
    public async Task Proposals_outlive_the_process_that_kept_them()
    {
        var folder = await StagedAsync(new ServerStaging(_users, _engine));
        var afterRestart = new ServerStaging(_users, _engine);
        var id = Path.GetFileName(folder);

        var change = afterRestart.Open(As("alice"), _workspace, id).Files.Single().Id;
        Assert.StartsWith("Rejected", await afterRestart.ActAsync(As("alice"), _workspace, id, change, apply: false, CancellationToken.None));

        Assert.Equal("old\n", File.ReadAllText(Path.Combine(_workspace, "notes.md")));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "staged change rejected" });
    }

    [Fact]
    public async Task Nothing_is_applied_while_its_run_is_still_writing()
    {
        var folder = await StagedAsync(end: false);
        var id = Path.GetFileName(folder);
        var change = _staging.Open(As("alice"), _workspace, id).Files.Single().Id;

        Assert.True(Assert.Single(_staging.List(As("alice"))).Running);
        Assert.Contains("still going", await _staging.ActAsync(As("alice"), _workspace, id, change, apply: true, CancellationToken.None));
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(_workspace, "notes.md")));
    }

    [Fact]
    public async Task A_run_that_proposed_nothing_keeps_nothing()
    {
        var folder = Begin(_staging);

        await EndAsync(_staging, folder, new StagingArtifactStore(_workspace, folder));

        Assert.False(Directory.Exists(folder));
        Assert.Empty(await InboxStoreFactory.Create(WorkspaceInfo.For(_workspace)).LoadAllAsync(CancellationToken.None));
    }

    /// <summary>The file was edited after the proposal: applying would erase that edit - the store's own check.</summary>
    [Fact]
    public async Task A_proposal_whose_file_changed_since_is_not_applied()
    {
        var folder = await StagedAsync();
        var id = Path.GetFileName(folder);
        File.WriteAllText(Path.Combine(_workspace, "notes.md"), "edited by hand\n");

        var said = await _staging.ActAsync(As("alice"), _workspace, id, _staging.Open(As("alice"), _workspace, id).Files.Single().Id,
            apply: true, CancellationToken.None);

        Assert.StartsWith("Not applied", said);
        Assert.Equal("edited by hand\n", File.ReadAllText(Path.Combine(_workspace, "notes.md")));
    }

    /// <summary>Only a run id names a set: nothing typed into the address walks out of the staging folder.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData(@"..\..\settings")]
    [InlineData("not-a-run")]
    public void A_set_is_named_only_by_a_run_id(string id)
        => Assert.Contains("no longer kept", _staging.Open(As("alice"), _workspace, id).Problem);

    // ── how long proposals are kept ─────────────────────────────────────────

    /// <summary>
    /// Proposals nobody decided in their time are dropped - the files stay as they are - and that is said where people
    /// look: the inbox and the audit trail. They waited without end, a folder of bytes per forgotten run in the workspace.
    /// </summary>
    [Fact]
    public async Task Proposals_nobody_decided_in_their_time_are_dropped_and_said()
    {
        var folder = await StagedAsync();

        var dropped = await _staging.ExpireAsync(DateTimeOffset.Now.AddDays(31), TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(1, dropped);
        Assert.False(Directory.Exists(folder));
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(_workspace, "notes.md")));
        Assert.Empty(_staging.List(As("vera")));
        var items = await InboxStoreFactory.Create(WorkspaceInfo.For(_workspace)).LoadAllAsync(CancellationToken.None);
        Assert.Contains(items, i => i.Summary.Contains("not decided in 30 days", StringComparison.Ordinal));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "(service)", Action: "staged changes dropped" });
    }

    /// <summary>Within their time they stay; and a run still writing its proposals is never dropped from under it.</summary>
    [Fact]
    public async Task Proposals_within_their_time_and_a_run_still_writing_are_kept()
    {
        var waiting = await StagedAsync();
        var writing = await StagedAsync(end: false);

        Assert.Equal(0, await _staging.ExpireAsync(DateTimeOffset.Now.AddDays(29), TimeSpan.FromDays(30), CancellationToken.None));
        Assert.Equal(1, await _staging.ExpireAsync(DateTimeOffset.Now.AddDays(31), TimeSpan.FromDays(30), CancellationToken.None));

        Assert.False(Directory.Exists(waiting));
        Assert.True(Directory.Exists(writing));
    }
}
