namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

public sealed class RemainingDurabilityEncodingTests
{
    [Fact]
    public async Task Compare_does_not_misread_BOMless_utf16_as_utf8()
    {
        using var fx = new EngineFixture();
        File.WriteAllBytes(fx.PathOf("a.txt"), new UnicodeEncoding(false, false).GetBytes("text"));
        fx.Write("b.txt", "text");
        var result = await fx.Invoke(new CompareFilesTool(), """{"a":"a.txt","b":"b.txt"}""");
        Assert.False(result.Success);
        Assert.Contains("BOM", result.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Compare_rejects_invalid_bytes_instead_of_calling_two_replacement_characters_equal(bool staged, bool utf16)
    {
        using var fx = new EngineFixture();
        using var proposals = new StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals.BeginStep() : fx.Artifacts;
        byte[] a = utf16 ? [0xff, 0xfe, 0, 0xd8] : [0xff];
        byte[] b = utf16 ? [0xff, 0xfe, 1, 0xd8] : [0xfe];
        await store.CreateAsync("a.txt", ArtifactKind.FileSet, "text/plain", s => s.WriteAsync(a).AsTask(), default);
        await store.CreateAsync("b.txt", ArtifactKind.FileSet, "text/plain", s => s.WriteAsync(b).AsTask(), default);
        var result = await fx.Invoke(new CompareFilesTool(), """{"a":"a.txt","b":"b.txt"}""", store);
        Assert.False(result.Success);
        Assert.Contains("Unicode", result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compare_reads_unicode_BOMs_and_new_staged_files(bool staged)
    {
        using var fx = new EngineFixture();
        using var proposals = new StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals.BeginStep() : fx.Artifacts;
        const string text = "Hello — Привет 日本語";
        byte[] a = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
        byte[] b = [.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes(text)];
        await store.CreateAsync("a.txt", ArtifactKind.FileSet, "text/plain", s => s.WriteAsync(a).AsTask(), default);
        await store.CreateAsync("b.txt", ArtifactKind.FileSet, "text/plain", s => s.WriteAsync(b).AsTask(), default);
        var result = await fx.Invoke(new CompareFilesTool(), """{"a":"a.txt","b":"b.txt"}""", store);
        Assert.True(result.Success, result.Error);
        Assert.Equal(true, result.Metadata!["identical"]);
    }

    [Fact]
    public async Task Resuming_the_same_run_does_not_overwrite_its_previous_backup()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "original");
        var run = Guid.NewGuid();
        var first = new DiskArtifactStore(fx.Workspace, run);
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","content":"first"}""", first.BeginStep());
        var backup = Assert.Single(first.Writes).BackupPath!;
        var resumed = new DiskArtifactStore(fx.Workspace, run);
        var scope = resumed.BeginStep();
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","content":"second"}""", scope);
        var next = Assert.Single(resumed.Writes).BackupPath!;
        Assert.NotEqual(backup, next);
        Assert.Equal("original", File.ReadAllText(backup));
        Assert.Equal("first", File.ReadAllText(next));
        Assert.Contains("a.txt", (await scope.RevertAsync(["a.txt"], default)).Reverted);
        Assert.Equal("first", fx.Read("a.txt"));
        Assert.Equal("original", File.ReadAllText(backup));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"Path\":")]
    public void Interrupted_preparation_is_cleaned_without_changing_the_workspace(string partial)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base");
        var folder = fx.PathOf(".enactive/undo/interrupted");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "x.append.preparing"), partial);
        File.WriteAllText(Path.Combine(folder, "x.append.tail"), "partial tail");
        var store = new DiskArtifactStore(fx.Workspace);
        Assert.Empty(store.AppendRecoveryConflicts);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Equal("base", fx.Read("a.txt"));
    }

    [Fact]
    public void Live_preparation_is_left_alone_and_orphan_committed_tail_is_cleaned()
    {
        using var fx = new EngineFixture();
        var folder = fx.PathOf(".enactive/undo/interrupted");
        Directory.CreateDirectory(folder);
        var preparing = Path.Combine(folder, "x.append.preparing");
        var tail = Path.Combine(folder, "x.append.tail");
        using (new FileStream(preparing, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete))
        {
            File.WriteAllText(tail, "tail");
            _ = new DiskArtifactStore(fx.Workspace);
            Assert.True(File.Exists(preparing));
            Assert.True(File.Exists(tail));
        }
        File.Delete(preparing); // Models commit cleanup interrupted between the two deletes.
        _ = new DiskArtifactStore(fx.Workspace);
        Assert.False(File.Exists(tail));
    }
}
