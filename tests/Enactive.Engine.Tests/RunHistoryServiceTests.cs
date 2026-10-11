namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The history of a workspace as every host reads and prunes it (RunHistoryService) - what the window did in its
/// code-behind, between dialogs, and the service must do for people in a browser, with the same questions.
/// </summary>
public sealed class RunHistoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-history", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceInfo _workspace;

    public RunHistoryServiceTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static RunRecord Record(string title, DateTimeOffset at, Guid? taskId = null, string? request = null, params string[] artifacts)
        => new(Guid.NewGuid(), taskId ?? Guid.NewGuid(), title, "a-model", at, at.AddSeconds(5), "Completed",
            [new RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent: " + title,
                Payload: request is null ? null : WorkEventPayload.RequestPayload(request))],
            artifacts, []);

    private async Task<RunRecord> SavedAsync(string title, int minutesAgo = 0, params string[] artifacts)
    {
        var record = Record(title, DateTimeOffset.Now.AddMinutes(-minutesAgo), artifacts: artifacts);
        await RunStoreFactory.Create(_workspace).SaveAsync(record, CancellationToken.None);
        return record;
    }

    // ── reading ─────────────────────────────────────────────────────────────

    /// <summary>A count, applied where the list is read: the newest stay, the rest leave the store too.</summary>
    [Fact]
    public async Task The_list_keeps_the_newest_runs_the_setting_asks_for()
    {
        foreach (var age in new[] { 40, 30, 20, 10 })
            await SavedAsync($"{age} minutes ago", age);

        var history = await new RunHistoryService(() => 2).LoadAsync(_root, CancellationToken.None);

        Assert.Null(history.Problem);
        Assert.Equal(["10 minutes ago", "20 minutes ago"], history.Runs.Select(r => r.Title).OrderBy(t => t));
        Assert.Equal(2, (await RunStoreFactory.Create(_workspace).LoadSummariesAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Keeping_none_keeps_them_all()
    {
        foreach (var age in new[] { 3, 2, 1 })
            await SavedAsync($"run {age}", age);

        Assert.Equal(3, (await new RunHistoryService(() => 0).LoadAsync(_root, CancellationToken.None)).Runs.Count);
    }

    [Fact]
    public async Task A_run_no_longer_in_the_store_is_said_by_its_title()
    {
        var gone = Record("the digest", DateTimeOffset.Now);
        var summary = (await SavedThenSummaryAsync(gone));
        await RunStoreFactory.Create(_workspace).DeleteAsync(gone.RunId, CancellationToken.None);

        var (record, problem) = await new RunHistoryService(() => 0).OpenAsync(_root, summary, CancellationToken.None);

        Assert.Null(record);
        Assert.Contains("no longer in the store", problem);
        Assert.Contains("the digest", problem);
    }

    private async Task<RunSummary> SavedThenSummaryAsync(RunRecord record)
    {
        var store = RunStoreFactory.Create(_workspace);
        await store.SaveAsync(record, CancellationToken.None);
        return Assert.Single(await store.LoadSummariesAsync(CancellationToken.None), s => s.RunId == record.RunId);
    }

    // ── forgetting runs ─────────────────────────────────────────────────────

    /// <summary>What goes is the record; the files are the person's work, and the question says they stay.</summary>
    [Fact]
    public async Task Deleting_runs_asks_with_their_count_and_says_their_files_stay()
    {
        await SavedAsync("one", 2, "a.md");
        await SavedAsync("two", 1, "b.md", "c.md");
        var runs = (await new RunHistoryService(() => 0).LoadAsync(_root, CancellationToken.None)).Runs;

        var question = RunHistoryService.DeleteMany(runs);

        Assert.Equal("Delete 2 runs?", question.Headline);
        Assert.Contains("The 3 file(s) they wrote stay in your workspace", question.Body);
        Assert.Equal("Delete 2", question.Confirm);
        Assert.Contains("“one”", RunHistoryService.DeleteOne(runs.Single(r => r.Title == "one")).Headline);
    }

    [Fact]
    public async Task Deleted_runs_leave_the_store_and_their_files_stay()
    {
        File.WriteAllText(Path.Combine(_root, "a.md"), "work");
        await SavedAsync("one", 2, "a.md");
        await SavedAsync("two", 1);
        var service = new RunHistoryService(() => 0);
        var runs = (await service.LoadAsync(_root, CancellationToken.None)).Runs;

        Assert.Null(await service.DeleteAsync(_root, runs, CancellationToken.None));

        Assert.Empty((await service.LoadAsync(_root, CancellationToken.None)).Runs);
        Assert.True(File.Exists(Path.Combine(_root, "a.md")));
    }

    // ── a file a past run wrote ─────────────────────────────────────────────

    /// <summary>
    /// A record names its files relative to the folder. One that climbs out of it is not this workspace's file to show
    /// or delete - and on the service, the person reading the record may not have that folder at all.
    /// </summary>
    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("../outside.txt")]
    [InlineData("sub\\..\\..\\outside.txt")]
    public void A_file_outside_the_workspace_is_not_the_workspace_s_to_delete(string relative)
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt");
        File.WriteAllText(outside, "somebody else's");
        try
        {
            Assert.Null(RunHistoryService.FileOf(_root, relative));
            Assert.Null(RunHistoryService.DeleteFile(_root, relative));
            Assert.Equal(("not in this workspace", true), RunHistoryService.DeleteFileNow(_root, relative));
            Assert.True(File.Exists(outside));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void A_file_is_deleted_as_it_is_now_after_a_question_that_gives_its_date()
    {
        var file = Path.Combine(_root, "notes.md");
        File.WriteAllText(file, "edited since");

        var question = RunHistoryService.DeleteFile(_root, "notes.md");

        Assert.NotNull(question);
        Assert.Contains($"last changed {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}", question.Body);
        Assert.Equal(("deleted", true), RunHistoryService.DeleteFileNow(_root, "notes.md"));
        Assert.False(File.Exists(file));
        Assert.Null(RunHistoryService.DeleteFile(_root, "notes.md"));
        Assert.Equal(("already gone", true), RunHistoryService.DeleteFileNow(_root, "notes.md"));
    }

    // ── starting a past run again ───────────────────────────────────────────

    /// <summary>Attempts at ONE task keep its id; a record from before task ids meant anything gets a new one.</summary>
    [Fact]
    public void A_typed_run_is_tried_again_with_its_own_request_as_the_same_task()
    {
        var task = Guid.NewGuid();
        var again = RunHistoryService.Retry(Record("t", DateTimeOffset.Now, task, request: "summarise the inbox"));

        Assert.Equal(new Rerun("summarise the inbox", null, task), again);
        Assert.NotEqual(Guid.Empty, RunHistoryService.Retry(Record("t", DateTimeOffset.Now, Guid.Empty, request: "x"))!.TaskId);
    }

    [Fact]
    public void A_run_with_no_request_and_no_task_is_nothing_to_try_again()
    {
        Assert.Null(RunHistoryService.Retry(Record("t", DateTimeOffset.Now)));
        Assert.Equal((null, null), RunHistoryService.RunAgain(_root, Record("t", DateTimeOffset.Now), new Enactive.Core.Permissions.PermissionPolicy(Enactive.Core.Permissions.PermissionLevel.Autonomous, ["*"], [])));
    }

    // ── an unfinished run ───────────────────────────────────────────────────

    [Fact]
    public async Task An_unfinished_run_with_no_record_of_its_files_is_forgotten_after_a_plain_question()
    {
        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "tidy the wiki", "tidy the wiki", null, null,
            [new CheckpointStep(Guid.NewGuid(), "first", [], "low", "Done"), new CheckpointStep(Guid.NewGuid(), "second", [], "low", "Pending")],
            [], [], [], 1, 0);
        await new JsonCheckpointStore(_workspace).SaveAsync(checkpoint, CancellationToken.None);

        var question = RunHistoryService.ForgetUnfinished(_root, checkpoint);
        Assert.Null(question.Alternative);
        Assert.Contains("the 1 step(s) left will not be run", question.Body);

        var (kept, problem) = await new RunHistoryService(() => 0).ForgetUnfinishedAsync(_root, checkpoint, putBack: false, CancellationToken.None);

        Assert.Null(kept);
        Assert.Null(problem);
        Assert.Empty(await new JsonCheckpointStore(_workspace).LoadAllAsync(CancellationToken.None));
    }
}
