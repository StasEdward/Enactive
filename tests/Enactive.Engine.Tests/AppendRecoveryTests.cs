namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Workspace;
using Xunit;

public sealed class AppendRecoveryTests
{
    [Theory]
    [InlineData("ta")]
    [InlineData("tail")]
    public async Task Interrupted_partial_or_full_append_is_restored_on_next_store_open(string written)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base");
        var directory = fx.PathOf(".enactive/undo/interrupted");
        using (var tail = new MemoryStream(Encoding.UTF8.GetBytes("tail")))
        using (var intent = await AppendIntent.PrepareAsync(directory, "a.txt", 4,
            FileHash.OfFile(fx.PathOf("a.txt"))!, tail, default))
            File.AppendAllText(fx.PathOf("a.txt"), written);
        // Closing the handle without Complete models termination before the commit point.
        var reopened = new DiskArtifactStore(fx.Workspace);
        Assert.Empty(reopened.AppendRecoveryConflicts);
        Assert.Equal("base", fx.Read("a.txt"));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Theory]
    [InlineData("EDITta")]
    [InlineData("baseXX")]
    public async Task Recovery_does_not_discard_external_prefix_or_tail_edits(string edited)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base");
        var directory = fx.PathOf(".enactive/undo/interrupted");
        using (var tail = new MemoryStream(Encoding.UTF8.GetBytes("tail")))
        using (var intent = await AppendIntent.PrepareAsync(directory, "a.txt", 4,
            FileHash.OfFile(fx.PathOf("a.txt"))!, tail, default))
            fx.Write("a.txt", edited);
        var reopened = new DiskArtifactStore(fx.Workspace);
        Assert.Contains("a.txt", reopened.AppendRecoveryConflicts);
        Assert.Equal(edited, fx.Read("a.txt"));
        Assert.NotEmpty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task Live_append_is_not_recovered_by_a_second_store_and_commit_clears_intent()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base");
        var directory = fx.PathOf(".enactive/undo/active");
        using var tail = new MemoryStream(Encoding.UTF8.GetBytes("tail"));
        using var intent = await AppendIntent.PrepareAsync(directory, "a.txt", 4,
            FileHash.OfFile(fx.PathOf("a.txt"))!, tail, default);
        File.AppendAllText(fx.PathOf("a.txt"), "tail");
        _ = new DiskArtifactStore(fx.Workspace);
        Assert.Equal("basetail", fx.Read("a.txt"));
        intent.Complete();
        _ = new DiskArtifactStore(fx.Workspace);
        Assert.Equal("basetail", fx.Read("a.txt"));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }
}
