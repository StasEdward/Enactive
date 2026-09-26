namespace Enactive.Engine.Tests;

using Enactive.Workspace;
using Xunit;

public sealed class IncrementalFingerprintTests
{
    [Fact]
    public async Task Unchanged_files_reuse_hashes_but_external_edits_with_restored_mtime_do_not()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "one");
        using var changes = new WorkspaceChanges(fx.Root);
        var before = await changes.TakeAsync(default);
        Assert.NotNull(before);
        var count = changes.HashComputations;
        Assert.True(count > 0);
        var same = await changes.TakeAsync(default);
        Assert.NotNull(same);
        if (changes.ReusesFingerprints) Assert.Equal(count, changes.HashComputations);
        else Assert.True(changes.HashComputations > count);
        var time = File.GetLastWriteTimeUtc(fx.PathOf("a.txt"));
        fx.Write("a.txt", "two");
        File.SetLastWriteTimeUtc(fx.PathOf("a.txt"), time);
        var edited = await changes.TakeAsync(default);
        Assert.NotNull(edited);
        Assert.Contains((await changes.ComparePathsAsync(before, edited, default))!, c => c.Path == "a.txt");
        Assert.True(changes.HashComputations > count);
    }

    [Fact]
    public async Task Atomic_replacement_rename_and_deletion_cannot_reuse_the_old_identity()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "one");
        using var changes = new WorkspaceChanges(fx.Root);
        var before = await changes.TakeAsync(default);
        var time = File.GetLastWriteTimeUtc(fx.PathOf("a.txt"));
        fx.Write("tmp.txt", "two");
        File.SetLastWriteTimeUtc(fx.PathOf("tmp.txt"), time);
        File.Move(fx.PathOf("tmp.txt"), fx.PathOf("a.txt"), true);
        var after = await changes.TakeAsync(default);
        Assert.Contains((await changes.ComparePathsAsync(before!, after!, default))!, c => c.Path == "a.txt");
        File.Move(fx.PathOf("a.txt"), fx.PathOf("b.txt"));
        var renamed = await changes.TakeAsync(default);
        Assert.NotNull(renamed);
        Assert.DoesNotContain("a.txt", renamed.Files!.Keys);
        Assert.Contains("b.txt", renamed.Files.Keys);
        File.Delete(fx.PathOf("b.txt"));
        var deleted = await changes.TakeAsync(default);
        Assert.NotNull(deleted);
        Assert.DoesNotContain("b.txt", deleted.Files!.Keys);
    }
}
