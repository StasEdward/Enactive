namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Enactive.Core.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The file an interrupted run leaves behind.
///
/// <para>There is one implementation and it is file-backed, which is a decision rather than an
/// unfinished job: a checkpoint describes a process that died on THIS machine with THIS folder
/// half-changed, and it is worth nothing anywhere else. A workspace whose history lives in MySQL
/// still keeps its checkpoints beside the files they are about.</para>
/// </summary>
public sealed class CheckpointStoreTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceInfo _workspace;

    public CheckpointStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string Folder => Path.Combine(_root, ".enactive", "checkpoints");

    [Fact]
    public async Task Cancelled_replacement_keeps_previous_checkpoint_and_removes_its_temporary_file()
    {
        var store = new JsonCheckpointStore(_workspace);
        var original = Checkpoint("original");
        await store.SaveAsync(original, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            original with { Request = new string('x', 100000) }, new CancellationToken(true)));
        Assert.Equal("original", (await store.LoadAsync(original.RunId, default))!.Request);
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_store_instances_use_distinct_temporaries_and_publish_whole_snapshots()
    {
        var original = Checkpoint("original");
        await new JsonCheckpointStore(_workspace).SaveAsync(original, default);
        var texts = Enumerable.Range(0, 12).Select(i => $"{i}:" + new string((char)('a' + i), 50000)).ToArray();
        await Task.WhenAll(texts.Select(text => new JsonCheckpointStore(_workspace)
            .SaveAsync(original with { Request = text }, default)));
        Assert.Contains((await new JsonCheckpointStore(_workspace).LoadAsync(original.RunId, default))!.Request, texts);
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));
    }

    [WindowsFact]
    public async Task Cancellation_during_replace_retry_keeps_the_previous_checkpoint()
    {
        var store = new JsonCheckpointStore(_workspace);
        var original = Checkpoint("original");
        await store.SaveAsync(original, default);
        using (new FileStream(Path.Combine(Folder, $"{original.RunId:N}.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
                original with { Request = "replacement" }, stop.Token));
        Assert.Equal("original", (await store.LoadAsync(original.RunId, default))!.Request);
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));
    }

    private static RunCheckpoint Checkpoint(
        string request = "do the thing",
        int minutesAgo = 0,
        bool staged = false,
        Guid runId = default,
        Guid taskId = default)
    {
        var at = DateTimeOffset.Now.AddMinutes(-minutesAgo);
        var stepId = Guid.NewGuid();
        return new RunCheckpoint(
            runId == default ? Guid.NewGuid() : runId,
            taskId == default ? Guid.NewGuid() : taskId,
            at.AddMinutes(-5),
            at,
            request,
            "a plan",
            "developer",
            Spec: null,
            Steps: new[]
            {
                new CheckpointStep(stepId, "first", Array.Empty<Guid>(), nameof(StepComplexity.Normal),
                                   nameof(StepStatus.Done), nameof(StepOutcomeKind.Succeeded)),
                new CheckpointStep(Guid.NewGuid(), "second", new[] { stepId }, nameof(StepComplexity.Complex),
                                   nameof(StepStatus.Pending))
            },
            Digest: new[] { "first: wrote the notes" },
            Transcript: new[]
            {
                ChatMessage.System("you are a developer"),
                ChatMessage.User("do the thing"),
                ChatMessage.Assistant(null, new[] { new ToolCall("c1", "write_file", """{"path":"a.txt"}""") }),
                ChatMessage.Tool("c1", "wrote a.txt")
            },
            Artifacts: new[] { "a.txt" },
            StepsRun: 1,
            TokensSpent: 420,
            Settings: new RunSettings(2, "Assisted", "developer", staged));
    }

    // ── the round trip ──────────────────────────────────────────────────────

    /// <summary>
    /// Everything comes back, including the transcript's tool calls. That last part is the one worth
    /// a test of its own: a conversation whose assistant turns lost their tool calls would replay as
    /// a model that talked about writing files and never wrote any.
    /// </summary>
    [Fact]
    public async Task A_checkpoint_survives_the_round_trip_whole()
    {
        var store = new JsonCheckpointStore(_workspace);
        var written = Checkpoint();

        await store.SaveAsync(written, CancellationToken.None);
        var back = await store.LoadAsync(written.RunId, CancellationToken.None);

        Assert.NotNull(back);
        Assert.Equal(written.RunId, back!.RunId);
        Assert.Equal(written.TaskId, back.TaskId);
        Assert.Equal(written.Request, back.Request);
        Assert.Equal(written.Title, back.Title);
        Assert.Equal(written.WorkerId, back.WorkerId);
        Assert.Equal(written.Digest, back.Digest);
        Assert.Equal(written.Artifacts, back.Artifacts);
        Assert.Equal(written.StepsRun, back.StepsRun);
        Assert.Equal(written.TokensSpent, back.TokensSpent);
        Assert.Equal(written.Settings, back.Settings);

        Assert.Equal(2, back.Steps.Count);
        Assert.Equal("first", back.Steps[0].Title);
        Assert.Equal(nameof(StepStatus.Done), back.Steps[0].Status);
        Assert.Equal(nameof(StepOutcomeKind.Succeeded), back.Steps[0].Outcome);
        Assert.Equal(written.Steps[1].DependsOn, back.Steps[1].DependsOn);
        Assert.Equal(nameof(StepComplexity.Complex), back.Steps[1].Complexity);
        Assert.Null(back.Steps[1].Outcome);

        Assert.Equal(4, back.Transcript.Count);
        Assert.Equal(ChatRole.System, back.Transcript[0].Role);
        var call = Assert.Single(back.Transcript[2].ToolCalls!);
        Assert.Equal("write_file", call.Name);
        Assert.Equal("c1", back.Transcript[3].ToolCallId);
    }

    /// <summary>
    /// A run's later checkpoint replaces its earlier one. One file per run, because a resume needs
    /// the LAST thing that was true and a folder of every boundary a long run passed would be a
    /// history nobody asked for.
    /// </summary>
    [Fact]
    public async Task A_later_checkpoint_replaces_the_one_before_it()
    {
        var store = new JsonCheckpointStore(_workspace);
        var runId = Guid.NewGuid();

        await store.SaveAsync(Checkpoint("early", minutesAgo: 5, runId: runId), CancellationToken.None);
        await store.SaveAsync(Checkpoint("late", runId: runId), CancellationToken.None);

        var all = await store.LoadAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("late", all[0].Request);
        Assert.Single(Directory.GetFiles(Folder, "*.json"));
    }

    [Fact]
    public async Task Checkpoints_come_back_newest_first()
    {
        var store = new JsonCheckpointStore(_workspace);
        foreach (var request in new[] { "oldest", "newest", "middle" })
            await store.SaveAsync(
                Checkpoint(request, minutesAgo: request switch { "oldest" => 30, "middle" => 10, _ => 1 }),
                CancellationToken.None);

        var all = await store.LoadAllAsync(CancellationToken.None);
        Assert.Equal(new[] { "newest", "middle", "oldest" }, all.Select(c => c.Request).ToArray());
    }

    [Fact]
    public async Task A_workspace_with_no_interrupted_runs_has_none()
        => Assert.Empty(await new JsonCheckpointStore(_workspace).LoadAllAsync(CancellationToken.None));

    [Fact]
    public async Task Deleting_a_checkpoint_that_is_not_there_is_not_a_failure()
    {
        var store = new JsonCheckpointStore(_workspace);
        var written = Checkpoint();

        await store.SaveAsync(written, CancellationToken.None);
        await store.DeleteAsync(written.RunId, CancellationToken.None);
        await store.DeleteAsync(written.RunId, CancellationToken.None);
        await store.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(await store.LoadAllAsync(CancellationToken.None));
    }

    // ── what it refuses ─────────────────────────────────────────────────────

    /// <summary>
    /// A staged run is not recorded, and the refusal lives HERE rather than at each call site, so no
    /// host can forget it. Staged changes live in memory; a resumed staged run would apply later
    /// steps on top of earlier ones that were never written.
    /// </summary>
    [Fact]
    public async Task A_staged_run_is_refused()
    {
        var store = new JsonCheckpointStore(_workspace);

        await store.SaveAsync(Checkpoint(staged: true), CancellationToken.None);

        Assert.Empty(await store.LoadAllAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Folder) && Directory.GetFiles(Folder, "*.json").Length > 0);
    }

    /// <summary>
    /// A checkpoint that cannot be read is not offered. Salvaging what parses would hand somebody a
    /// plan with an unknown number of its steps missing and call it their run - a resume that cannot
    /// happen is a disappointment, and one from a plan with holes in it does damage.
    /// </summary>
    [Fact]
    public async Task An_unreadable_checkpoint_is_not_offered()
    {
        var store = new JsonCheckpointStore(_workspace);
        var good = Checkpoint("this one is fine");
        await store.SaveAsync(good, CancellationToken.None);
        await store.SaveAsync(Checkpoint("this one will be broken"), CancellationToken.None);

        var broken = Directory.GetFiles(Folder, "*.json")
            .First(f => File.ReadAllText(f).Contains("will be broken", StringComparison.Ordinal));
        File.WriteAllText(broken, "{ this is not json");

        var all = await store.LoadAllAsync(CancellationToken.None);
        var only = Assert.Single(all);
        Assert.Equal("this one is fine", only.Request);
    }

    // ── what it says about itself ───────────────────────────────────────────

    /// <summary>
    /// A checkpoint with nothing left to do is not resumable. It is a run that finished its steps
    /// and died somewhere after them; re-running nothing is not a resume, and offering the button
    /// would be offering an action that does nothing.
    /// </summary>
    [Fact]
    public void A_checkpoint_with_every_step_finished_is_not_resumable()
    {
        var done = Checkpoint();
        var all = done with
        {
            Steps = done.Steps.Select(s => s with { Status = nameof(StepStatus.Done) }).ToArray()
        };

        Assert.False(all.IsResumable);
        Assert.Equal(0, all.Remaining);
        Assert.Equal(2, all.Finished);
    }

    /// <summary>
    /// A step that was RUNNING counts as remaining. Nobody is running it, and calling it finished
    /// would let a resume build everything after it on work that never completed.
    /// </summary>
    [Fact]
    public void A_step_that_was_running_still_has_to_be_done()
    {
        var checkpoint = Checkpoint();
        var interrupted = checkpoint with
        {
            Steps = checkpoint.Steps
                .Select(s => s.Title == "second" ? s with { Status = nameof(StepStatus.Running) } : s)
                .ToArray()
        };

        Assert.True(interrupted.IsResumable);
        Assert.Equal(1, interrupted.Remaining);
    }

    /// <summary>
    /// A status name this build does not know reads as Pending, which means the step is DONE AGAIN.
    /// That is the safe direction on purpose: doing a step twice is recoverable, and silently
    /// dropping one is not.
    /// </summary>
    [Fact]
    public void An_unknown_status_is_treated_as_unfinished()
    {
        Assert.Equal(StepStatus.Pending, CheckpointNames.StatusOf("something-a-later-build-added"));
        Assert.Equal(StepStatus.Pending, CheckpointNames.StatusOf(null));
        Assert.Equal(StepStatus.Done, CheckpointNames.StatusOf("Done"));
        Assert.Null(CheckpointNames.OutcomeOf("not-an-outcome"));
        Assert.Equal(StepOutcomeKind.Succeeded, CheckpointNames.OutcomeOf("Succeeded"));
    }
}
