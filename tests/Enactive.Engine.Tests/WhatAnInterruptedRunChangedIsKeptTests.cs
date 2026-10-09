namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// 2026-10-09: a run was stopped by a restart halfway through a step that had broken a source file on purpose to check
/// a test. The store's record of what it displaced lived in memory, and went with the process: the copies were on disk,
/// and nothing could say which was which file. The break stayed in the workspace; the next run "fixed" it from a guess.
/// The record is now kept beside the copies, under the run's id; a run that carries another on puts back the steps it
/// does again, and reads "before the run" from the first attempt; and deleting an unfinished run can put back its files.
/// Deliberately not code: a notes folder.
/// </summary>
public sealed class WhatAnInterruptedRunChangedIsKeptTests
{
    private static readonly Guid StepA = Guid.NewGuid(), StepB = Guid.NewGuid();

    private static Task Write(IArtifactScope store, string path, string text)
        => store.CreateAsync(path, ArtifactKind.FileSet, path, s => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask(), default);

    /// <summary>
    /// The first attempt, as a process that then dies: step A finishes and writes a.txt; step B changes notes.txt the run
    /// found, and makes draft.txt. Nothing of it is kept in memory past this method.
    /// </summary>
    private static async Task<Guid> FirstAttempt(EngineFixture fx)
    {
        var runId = Guid.NewGuid();
        var store = new DiskArtifactStore(fx.Workspace);
        store.BeginRun(runId, null);
        await Write(store.BeginStep(StepA), "a.txt", "from step A");
        var b = store.BeginStep(StepB);
        await Write(b, "notes.txt", "BROKEN on purpose");
        await Write(b, "draft.txt", "half done");
        return runId;
    }

    [Fact]
    public async Task The_record_outlives_the_process_and_says_whose_each_write_was()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var runId = await FirstAttempt(fx);

        var writes = DiskArtifactStore.ReadChain(fx.Workspace.RootPath, runId);

        Assert.Equal(["a.txt", "notes.txt", "draft.txt"], writes.Select(w => w.RelativePath));
        Assert.Equal([StepA, StepB, StepB], writes.Select(w => w.Step!.Value));
        Assert.True(writes[1].ExistedBefore);
    }

    /// <summary>The steps a carried-on run does again are put back; a finished step's work stays.</summary>
    [Fact]
    public async Task Carrying_a_run_on_puts_back_what_its_interrupted_step_changed()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var first = await FirstAttempt(fx);

        var store = new DiskArtifactStore(fx.Workspace);
        store.BeginRun(Guid.NewGuid(), first);
        var report = await store.PutBackStepsAsync([StepB], default);

        Assert.Equal(["draft.txt", "notes.txt"], report.Reverted.Order());
        Assert.Equal("as found", fx.Read("notes.txt"));
        Assert.False(File.Exists(Path.Combine(fx.Workspace.RootPath, "draft.txt")));
        Assert.Equal("from step A", fx.Read("a.txt"));
    }

    /// <summary>A file changed since the step left it is not the step's to put back: it is left, and said.</summary>
    [Fact]
    public async Task A_file_changed_since_is_left_and_said()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var first = await FirstAttempt(fx);
        fx.Write("notes.txt", "a person's edit");

        var store = new DiskArtifactStore(fx.Workspace);
        store.BeginRun(Guid.NewGuid(), first);
        var report = await store.PutBackStepsAsync([StepB], default);

        Assert.Contains("notes.txt", report.Kept);
        Assert.Equal("a person's edit", fx.Read("notes.txt"));
    }

    /// <summary>A finished step that wrote the file after the interrupted one did owns it now: it is not put back.</summary>
    [Fact]
    public async Task A_file_a_finished_step_wrote_after_is_left()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var runId = Guid.NewGuid();
        var store = new DiskArtifactStore(fx.Workspace);
        store.BeginRun(runId, null);
        await Write(store.BeginStep(StepB), "notes.txt", "B's");
        await Write(store.BeginStep(StepA), "notes.txt", "A's, after");

        var next = new DiskArtifactStore(fx.Workspace);
        next.BeginRun(Guid.NewGuid(), runId);
        var report = await next.PutBackStepsAsync([StepB], default);

        Assert.Contains("notes.txt", report.Kept);
        Assert.Equal("A's, after", fx.Read("notes.txt"));
    }

    /// <summary>"Before the run" in a carried-on run is before the task's first attempt, for restore_file and the limits.</summary>
    [Fact]
    public async Task A_carried_on_run_knows_the_files_as_the_first_attempt_found_them()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var first = await FirstAttempt(fx);

        var store = new DiskArtifactStore(fx.Workspace);
        store.BeginRun(Guid.NewGuid(), first);

        var notes = await store.BeforeRunAsync("notes.txt", default);
        Assert.Equal((BeforeRunState.Kept, "as found"), (notes.State, Encoding.UTF8.GetString(notes.Content!)));
        Assert.Equal(BeforeRunState.Absent, (await store.BeforeRunAsync("draft.txt", default)).State);
    }

    // ── deleting an unfinished run ────────────────────────────────────────

    [Fact]
    public async Task An_unfinished_run_says_what_it_changed_and_can_put_it_all_back()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var first = await FirstAttempt(fx);

        var changes = DiskArtifactStore.ChangesOf(fx.Workspace, first);
        Assert.Equal([("a.txt", false), ("notes.txt", true), ("draft.txt", false)], changes.Select(c => (c.Path, c.ExistedBefore)));
        Assert.All(changes, c => Assert.True(c.AsLeft));

        var report = await DiskArtifactStore.PutBackRunAsync(fx.Workspace, first, default);

        Assert.Equal(3, report.Reverted.Count);
        Assert.Equal("as found", fx.Read("notes.txt"));
        Assert.False(File.Exists(Path.Combine(fx.Workspace.RootPath, "a.txt")));
    }

    [Fact]
    public void A_run_with_no_record_has_nothing_to_say()
        => Assert.Empty(DiskArtifactStore.ChangesOf(new EngineFixture().Workspace, Guid.NewGuid()));

    // ── through the engine ────────────────────────────────────────────────

    /// <summary>A run keeps its record under its own id, each write marked with the step of the plan that made it.</summary>
    [Fact]
    public async Task A_run_keeps_its_record_under_its_id_by_step()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"notes","steps":[{"title":"one","dependsOn":[]},{"title":"two","dependsOn":[0]}]}"""),
            Turn.Calls1("write_file", """{"path":"one.txt","content":"1"}""", "w1"), Turn.Says("done"),
            Turn.Calls1("write_file", """{"path":"two.txt","content":"2"}""", "w2"), Turn.Says("done"));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write two notes");

        var writes = DiskArtifactStore.ReadChain(fx.Workspace.RootPath, events[0].RunId);
        Assert.Equal(["one.txt", "two.txt"], writes.Select(w => w.RelativePath));
        Assert.All(writes, w => Assert.NotNull(w.Step));
        Assert.NotEqual(writes[0].Step, writes[1].Step);
    }

    /// <summary>
    /// Carried on from its checkpoint, the run puts back what its interrupted step had changed before doing the step again
    /// - and says so - while what its finished step wrote stays.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_does_its_interrupted_step_again_from_the_files_as_they_were()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var first = await FirstAttempt(fx);
        var checkpoint = new RunCheckpoint(first, Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now,
            "write the notes", "notes", "developer", Spec: null,
            Steps:
            [
                new CheckpointStep(StepA, "write a", [], "Normal", "Done", "Succeeded"),
                new CheckpointStep(StepB, "write the notes", [StepA], "Normal", "Running")
            ],
            Digest: ["write a: wrote a.txt"], Transcript: [ChatMessage.System("you are a developer"), ChatMessage.User("write the notes")],
            Artifacts: ["a.txt"], StepsRun: 1, TokensSpent: 100);
        string? notesWhenTheStepBegan = null;
        var worker = new FakeChatProvider(Turn.Says("Notes are fine as they are."))
        {
            Answering = _ => { notesWhenTheStepBegan ??= fx.Read("notes.txt"); return null; }
        };

        var events = await fx.ResumeAsync(fx.Build(worker, EngineFixture.Role("developer")), checkpoint);

        Assert.Equal("as found", notesWhenTheStepBegan);
        Assert.Contains(events, e => e.Kind == EventKind.ContextAssembled
            && e.Summary.StartsWith("Put back 2 file(s) the interrupted step(s) had changed", StringComparison.Ordinal));
        Assert.Equal("from step A", fx.Read("a.txt"));
    }

    /// <summary>
    /// A step that stopped at a question is carried on from where it stopped, and what it did stands - its files are not
    /// put back, or the conversation it carries on would describe work that is gone.
    /// </summary>
    [Fact]
    public async Task A_step_that_stopped_at_a_question_keeps_what_it_did()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var first = await FirstAttempt(fx);
        var taskId = Guid.NewGuid();
        new Enactive.Agents.TaskProgress(fx.Workspace.RootPath).Park(taskId,
            new Enactive.Agents.ParkedPosition(2, [ChatMessage.System("you are a developer"), ChatMessage.User("write the notes")], [], []));
        var checkpoint = new RunCheckpoint(first, taskId, DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now,
            "write the notes", "notes", "developer", Spec: null,
            Steps:
            [
                new CheckpointStep(StepA, "write a", [], "Normal", "Done", "Succeeded"),
                new CheckpointStep(StepB, "write the notes", [StepA], "Normal", "Running")
            ],
            Digest: [], Transcript: [ChatMessage.System("you are a developer"), ChatMessage.User("write the notes")],
            Artifacts: ["a.txt"], StepsRun: 1, TokensSpent: 100);

        var events = await fx.ResumeAsync(fx.Build(new FakeChatProvider(Turn.Says("Done.")), EngineFixture.Role("developer")), checkpoint);

        Assert.DoesNotContain(events, e => e.Summary.StartsWith("Put back", StringComparison.Ordinal));
        Assert.Equal("BROKEN on purpose", fx.Read("notes.txt"));
    }
}
