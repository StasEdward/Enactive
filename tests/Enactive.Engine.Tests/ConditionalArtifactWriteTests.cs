namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

public sealed class ConditionalArtifactWriteTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tool_refuses_to_overwrite_a_version_written_after_its_read(bool staged, bool edit)
    {
        using var fx = new EngineFixture();
        using var proposals = new StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals : fx.Artifacts;
        fx.Write("a.txt", "original");
        var mine = store.BeginStep();
        var sibling = store.BeginStep();
        var raced = new Interpose(mine, () => Write(sibling, "sibling"));
        var result = edit
            ? await fx.Invoke(new EditFileTool(), """{"path":"a.txt","old_string":"original","new_string":"mine"}""", raced)
            : await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","content":"mine"}""", raced);
        Assert.False(result.Success);
        Assert.Contains("changed since", result.Error);
        Assert.Equal("sibling", staged ? await store.TryReadPendingAsync("a.txt", default) : fx.Read("a.txt"));
        Assert.Empty(mine.TouchedPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_file_must_still_be_absent_at_commit(bool staged)
    {
        using var fx = new EngineFixture();
        using var proposals = new StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals : fx.Artifacts;
        var result = await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","content":"mine"}""",
            new Interpose(store, () => Write(store, "sibling")));
        Assert.False(result.Success);
        Assert.Equal("sibling", staged ? await store.TryReadPendingAsync("a.txt", default) : fx.Read("a.txt"));
    }

    [Fact]
    public async Task Revert_waits_for_active_write_and_then_keeps_the_siblings_committed_version()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "original");
        var mine = fx.Artifacts.BeginStep();
        await Write(mine, "mine");
        var sibling = fx.Artifacts.BeginStep();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = sibling.CreateAsync("a.txt", ArtifactKind.FileSet, "a", async stream =>
        {
            entered.SetResult();
            await release.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("sibling"));
        }, default);
        await entered.Task;
        var reverting = mine.RevertAsync(["a.txt"], default);
        try
        {
            Assert.False(reverting.IsCompleted);
            Assert.False(fx.Artifacts.Undo("a.txt").Undone);
        }
        finally { release.TrySetResult(); }
        await writing;
        Assert.Contains("a.txt", (await reverting).Kept);
        Assert.Equal("sibling", fx.Read("a.txt"));
    }

    [Fact]
    public async Task Deletion_after_read_prevents_recreating_the_stale_version()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "original");
        var mine = fx.Artifacts.BeginStep();
        var sibling = fx.Artifacts.BeginStep();
        var raced = new Interpose(mine, async () =>
        {
            await sibling.RemoveAsync("a.txt", default);
            return new ArtifactRef(Guid.NewGuid(), ArtifactKind.FileSet, "deleted", "a.txt");
        });
        var result = await fx.Invoke(new EditFileTool(),
            """{"path":"a.txt","old_string":"original","new_string":"mine"}""", raced);
        Assert.False(result.Success);
        Assert.Contains("changed since", result.Error);
        Assert.False(File.Exists(Path.Combine(fx.Root, "a.txt")));
        Assert.Empty(mine.TouchedPaths);
    }

    [WindowsFact]
    public async Task Failed_delete_does_not_publish_a_deletion_to_the_journal()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "original");
        var scope = fx.Artifacts.BeginStep();
        using var held = new FileStream(Path.Combine(fx.Root, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => scope.RemoveAsync("a.txt", default));
        Assert.Empty(scope.TouchedPaths);
        Assert.Equal("original", fx.Read("a.txt"));
    }

    private static Task<ArtifactRef> Write(IArtifactStore store, string text)
        => store.CreateAsync("a.txt", ArtifactKind.FileSet, "a", s => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask(), default);

    private sealed class Interpose(IArtifactStore inner, Func<Task<ArtifactRef>> beforeCommit) : IArtifactStore
    {
        public bool CanCheckVersion => true;
        public async Task<ArtifactRef> CreateCheckedAsync(string path, ArtifactKind kind, string title,
            Func<Stream, Task> write, ArtifactVersion expected, CancellationToken ct)
        {
            await beforeCommit();
            return await inner.CreateCheckedAsync(path, kind, title, write, expected, ct);
        }
        public Task<ArtifactRef> CreateAsync(string path, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
            => throw new InvalidOperationException("The tool must use a checked write.");
        public Task<Stream> OpenAsync(Guid id, CancellationToken ct) => inner.OpenAsync(id, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public Task<string?> TryReadPendingAsync(string path, CancellationToken ct) => inner.TryReadPendingAsync(path, ct);
        public Task<Stream?> TryOpenPendingAsync(string path, CancellationToken ct) => inner.TryOpenPendingAsync(path, ct);
    }
}
