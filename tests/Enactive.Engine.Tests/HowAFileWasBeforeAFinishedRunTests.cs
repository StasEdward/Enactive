namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What a finished run's file looked like before the run first changed it, read back from the run's record after the run
/// is over (<see cref="DiskArtifactStore.BeforeRunOfAsync"/>) - what a page reviewing the run diffs against. Only from the
/// record: a path it holds no write to is not known, rather than taken to have been as it is now; and whether the file
/// is still as the run left it, because a diff against an edit made after the run would be shown as the run's.
/// </summary>
public sealed class HowAFileWasBeforeAFinishedRunTests
{
    private static Task Write(IArtifactScope store, string path, string text)
        => store.CreateAsync(path, ArtifactKind.FileSet, path, s => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask(), default);

    /// <summary>A run that changed notes.txt (as found) twice and made draft.txt, over: nothing of it kept in memory.</summary>
    private static async Task<Guid> Run(EngineFixture fx)
    {
        var runId = Guid.NewGuid();
        var store = new DiskArtifactStore(fx.Workspace);
        store.BeginRun(runId, null);
        var step = store.BeginStep();
        await Write(step, "notes.txt", "first change");
        await Write(step, "notes.txt", "second change");
        await Write(step, "draft.txt", "made by the run");
        return runId;
    }

    [Fact]
    public async Task A_file_the_run_changed_reads_as_it_was_before_its_first_change()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var run = await Run(fx);

        var (before, asLeft) = await DiskArtifactStore.BeforeRunOfAsync(fx.Workspace, run, "notes.txt", default);

        Assert.Equal((BeforeRunState.Kept, "as found"), (before.State, Encoding.UTF8.GetString(before.Content!)));
        Assert.True(asLeft);
    }

    [Fact]
    public async Task A_file_the_run_made_was_not_there()
    {
        using var fx = new EngineFixture();
        var run = await Run(fx);

        var (before, asLeft) = await DiskArtifactStore.BeforeRunOfAsync(fx.Workspace, run, "draft.txt", default);

        Assert.Equal(BeforeRunState.Absent, before.State);
        Assert.True(asLeft);
    }

    [Fact]
    public async Task A_file_edited_after_the_run_is_not_as_the_run_left_it()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.txt", "as found");
        var run = await Run(fx);
        fx.Write("notes.txt", "a person's edit, after the run");

        var (_, asLeft) = await DiskArtifactStore.BeforeRunOfAsync(fx.Workspace, run, "notes.txt", default);

        Assert.False(asLeft);
    }

    [Fact]
    public async Task A_file_the_record_holds_no_write_to_has_no_known_before()
    {
        using var fx = new EngineFixture();
        fx.Write("other.txt", "never written by the run");
        var run = await Run(fx);

        Assert.Equal(BeforeRunState.Untouched, (await DiskArtifactStore.BeforeRunOfAsync(fx.Workspace, run, "other.txt", default)).Before.State);
        // And a run with no record at all - older than what is kept, or never one of this workspace's.
        Assert.Equal(BeforeRunState.Untouched,
            (await DiskArtifactStore.BeforeRunOfAsync(fx.Workspace, Guid.NewGuid(), "notes.txt", default)).Before.State);
    }
}
