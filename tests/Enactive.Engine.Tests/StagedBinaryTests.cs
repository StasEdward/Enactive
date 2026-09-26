namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

public sealed class StagedBinaryTests
{
    public static IEnumerable<object[]> Payloads()
    {
        yield return new object[] { Enumerable.Range(0, 256).Select(i => (byte)i).ToArray() };
        yield return new object[] { Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Привет\r\n")).ToArray() };
        yield return new object[] { Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Привет\r\n")).ToArray() };
        yield return new object[] { Array.Empty<byte>() };
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Copy_open_and_apply_preserve_every_byte(byte[] bytes)
    {
        using var fx = new EngineFixture();
        await File.WriteAllBytesAsync(fx.PathOf("source.bin"), bytes);
        var staging = new StagingArtifactStore(fx.Root);
        await fx.Invoke(new CopyFileTool(), """{"from":"source.bin","to":"copy.bin"}""", staging.BeginStep());
        var change = Assert.Single(staging.Changes);
        Assert.False(fx.Exists("copy.bin"));
        Assert.True(change.IsNew);
        await using (var opened = await staging.OpenAsync(change.Id, default))
        {
            Assert.False(opened.CanWrite);
            using var captured = new MemoryStream();
            await opened.CopyToAsync(captured);
            Assert.Equal(bytes, captured.ToArray());
        }
        Assert.True(staging.Apply(change.Id).Applied);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fx.PathOf("copy.bin")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fx.PathOf("source.bin")));
    }

    [Fact]
    public async Task Chained_binary_proposals_use_the_previous_proposals_exact_bytes()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);
        var first = await Stage(staging, "file.bin", new byte[] { 0xff, 0x00 });
        var second = await Stage(staging, "file.bin", new byte[] { 0xfe, 0x00 });
        Assert.False(staging.Apply(second.Id).Applied);
        Assert.True(staging.Apply(first.Id).Applied);
        Assert.True(staging.Apply(second.Id).Applied);
        Assert.Equal(new byte[] { 0xfe, 0x00 }, await File.ReadAllBytesAsync(fx.PathOf("file.bin")));
        Assert.All(staging.Changes, c => Assert.True(c.IsBinary));
    }

    [Fact]
    public async Task External_binary_edit_is_not_overwritten()
    {
        using var fx = new EngineFixture();
        await File.WriteAllBytesAsync(fx.PathOf("file.bin"), new byte[] { 0xff });
        var staging = new StagingArtifactStore(fx.Root);
        var change = await Stage(staging, "file.bin", new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(fx.PathOf("file.bin"), new byte[] { 0xfe });
        Assert.False(staging.Apply(change.Id).Applied);
        Assert.Equal(new byte[] { 0xfe }, await File.ReadAllBytesAsync(fx.PathOf("file.bin")));
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Scratch_write_through_preserves_bytes(byte[] bytes)
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);
        const string path = ".enactive/scratch/file.bin";
        await Stage(staging, path, bytes);
        Assert.Empty(staging.Changes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fx.PathOf(path)));
    }

    [Fact]
    public async Task Utf16_preview_is_text_but_apply_keeps_the_encoding()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Привет")).ToArray();
        var reference = await Stage(staging, "text.txt", bytes);
        var change = Assert.Single(staging.Changes);
        Assert.False(change.IsBinary);
        Assert.Equal("Привет", change.NewContent);
        Assert.Equal("Привет", await staging.TryReadPendingAsync("text.txt", default));
        Assert.True(staging.Apply(reference.Id).Applied);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fx.PathOf("text.txt")));
    }

    private static Task<ArtifactRef> Stage(StagingArtifactStore store, string path, byte[] bytes)
        => store.CreateAsync(path, ArtifactKind.FileSet, path,
            stream => stream.WriteAsync(bytes).AsTask(), default);
}
