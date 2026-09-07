namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Deleting a run from the history.
///
/// <para>Asked for because the list had grown past the point of being readable and there was no way
/// to remove anything from it. The store could save and load and nothing else, so the whole feature
/// starts at <see cref="IRunStore.DeleteAsync"/> and every implementation has to agree about what
/// it means — a run kept in MySQL and a run kept in a file are the same run to the person deleting
/// it.</para>
///
/// <para><b>What is deleted is the RECORD.</b> The files a run wrote stay exactly where they are:
/// they are the user's work sitting in their workspace, and somebody pruning a list of old runs is
/// tidying a list, not asking for their code back. The confirmation says so, and this file pins
/// it.</para>
/// </summary>
public sealed class RunDeletionTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceInfo _workspace;

    public RunDeletionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>MySQL is not covered: it needs a server, and a test that silently skips is worse
    /// than one that is not there. Its delete is the same statement with the workspace clause the
    /// rest of that store already uses.</summary>
    public static TheoryData<string> Stores => new() { "json", "sqlite" };

    private IRunStore StoreOf(string kind)
        => kind == "json" ? new JsonRunStore(_workspace) : new SqliteRunStore(_workspace);

    private static RunRecord Record(string title = "a run", params string[] artifacts)
    {
        var at = DateTimeOffset.Now;
        return new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), title, "a-model", at, at.AddSeconds(5), "Completed",
            new[] { new RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent: " + title, null, null) },
            artifacts, Array.Empty<string>());
    }

    // ── the deletion itself ─────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_deleted_run_is_gone(string kind)
    {
        var store = StoreOf(kind);
        var doomed = Record("delete me");

        await store.SaveAsync(doomed, CancellationToken.None);
        await store.DeleteAsync(doomed.RunId, CancellationToken.None);

        Assert.Empty(await store.LoadAllAsync(CancellationToken.None));
    }

    /// <summary>The one that matters more than the deletion: everything else stays.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Only_the_run_that_was_named_is_deleted(string kind)
    {
        var store = StoreOf(kind);
        var keep = Record("keep me");
        var doomed = Record("delete me");
        var alsoKeep = Record("keep me too");

        foreach (var record in new[] { keep, doomed, alsoKeep })
            await store.SaveAsync(record, CancellationToken.None);

        await store.DeleteAsync(doomed.RunId, CancellationToken.None);

        var left = await store.LoadAllAsync(CancellationToken.None);
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, r => r.RunId == doomed.RunId);
        Assert.Contains(left, r => r.RunId == keep.RunId);
        Assert.Contains(left, r => r.RunId == alsoKeep.RunId);
    }

    /// <summary>
    /// Deleting something that is not there is the goal, not an error. Two clicks on the same bin,
    /// or a run another window removed a moment ago, must not produce a dialog about it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Deleting_a_run_that_is_not_there_is_not_a_failure(string kind)
    {
        var store = StoreOf(kind);
        var record = Record();
        await store.SaveAsync(record, CancellationToken.None);

        await store.DeleteAsync(record.RunId, CancellationToken.None);
        await store.DeleteAsync(record.RunId, CancellationToken.None);
        await store.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(await store.LoadAllAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Deleting_from_a_store_that_has_never_been_written_to_is_not_a_failure(string kind)
    {
        await StoreOf(kind).DeleteAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Empty(await StoreOf(kind).LoadAllAsync(CancellationToken.None));
    }

    // ── what deletion does NOT touch ────────────────────────────────────────

    /// <summary>
    /// The work stays. A run record lists the paths it wrote; those files are the user's, they are
    /// in the workspace, and a history window is no place to delete them from. Somebody clearing a
    /// list of old runs is tidying a list.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task The_files_a_run_wrote_are_left_alone(string kind)
    {
        var store = StoreOf(kind);
        var written = Path.Combine(_root, "review.md");
        File.WriteAllText(written, "the findings");

        var record = Record("wrote a review", "review.md");
        await store.SaveAsync(record, CancellationToken.None);
        await store.DeleteAsync(record.RunId, CancellationToken.None);

        Assert.True(File.Exists(written), "deleting a run took the file it wrote with it");
        Assert.Equal("the findings", File.ReadAllText(written));
    }

    /// <summary>
    /// A file store keeps one run per file, named with the run id. Deleting one must remove exactly
    /// that file and leave the folder otherwise as it was — including anything that is not a run.
    /// </summary>
    [Fact]
    public async Task The_file_store_removes_one_file_and_nothing_beside_it()
    {
        var store = new JsonRunStore(_workspace);
        var keep = Record("keep");
        var doomed = Record("delete");

        await store.SaveAsync(keep, CancellationToken.None);
        await store.SaveAsync(doomed, CancellationToken.None);

        var folder = Path.Combine(_root, ".enactive", "runs");
        var stranger = Path.Combine(folder, "notes.txt");
        File.WriteAllText(stranger, "somebody's own file");

        await store.DeleteAsync(doomed.RunId, CancellationToken.None);

        Assert.Single(Directory.GetFiles(folder, "*.json"));
        Assert.True(File.Exists(stranger), "deleting a run took a neighbouring file with it");
    }
}
