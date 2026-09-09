namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// Copying a file, whole.
///
/// <para>This tool exists because copying was the one everyday file operation with nothing behind
/// it. A model asked to copy read the file and wrote the bytes back out; <c>read_file</c> stops at
/// 8000 characters, so an 11 KB page became an 8 KB one - with the closing tags added, so it
/// parsed, looked complete, and was missing a quarter of its body. The tests that matter here are
/// therefore about WHOLENESS and about not destroying anything, not about the happy path.</para>
/// </summary>
public sealed class CopyFileToolTests
{
    /// <summary>
    /// The decisive one: a file larger than the read cap copies whole. Read-then-write cannot do
    /// this, and the file it produces instead is the failure that led here.
    /// </summary>
    [Fact]
    public async Task A_file_larger_than_a_read_can_return_is_copied_whole()
    {
        using var fx = new EngineFixture();

        // Comfortably past ReadFileTool's 8000-character cap, and ending in something recognisable
        // so a truncated result cannot pass by accident.
        var original = string.Concat(Enumerable.Repeat("<p>a line of the document</p>\n", 500)) + "<!--END-->\n";
        fx.Write("big.html", original);

        var result = await fx.Invoke(new CopyFileTool(), """{"from":"big.html","to":"copy.html"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(original, fx.Read("copy.html"));
        Assert.Equal(original, fx.Read("big.html"));
    }

    /// <summary>The source stays. That is the whole difference from a move, and it is worth pinning.</summary>
    [Fact]
    public async Task The_original_is_left_alone()
    {
        using var fx = new EngineFixture();
        fx.Write("one.md", "the content\n");

        Assert.True((await fx.Invoke(new CopyFileTool(), """{"from":"one.md","to":"two.md"}""")).Success);

        Assert.True(fx.Exists("one.md"));
        Assert.True(fx.Exists("two.md"));
    }

    /// <summary>
    /// Bytes, never decoded. A copy that re-encodes its file hands back something else under a new
    /// name - and unlike a move it does not even delete the original to prove it, so the two
    /// diverge silently.
    /// </summary>
    [Fact]
    public async Task Bytes_survive_a_copy()
    {
        using var fx = new EngineFixture();
        var original = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x41, 0x00 };
        File.WriteAllBytes(fx.PathOf("asset.bin"), original);

        var result = await fx.Invoke(new CopyFileTool(), """{"from":"asset.bin","to":"copy.bin"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(original, File.ReadAllBytes(fx.PathOf("copy.bin")));
        Assert.Equal(6L, Assert.Contains("bytes", result.Metadata));
    }

    /// <summary>
    /// It refuses to overwrite. A copy that lands on an existing file is a deletion nobody asked
    /// for, and the one thing a copy must never be is destructive.
    /// </summary>
    [Fact]
    public async Task It_will_not_copy_onto_an_existing_file()
    {
        using var fx = new EngineFixture();
        fx.Write("one.md", "new\n");
        fx.Write("two.md", "precious\n");

        var result = await fx.Invoke(new CopyFileTool(), """{"from":"one.md","to":"two.md"}""");

        Assert.False(result.Success);
        Assert.Equal("precious\n", fx.Read("two.md"));
    }

    /// <summary>A copy onto itself is a mistake, and doing nothing quietly would hide it.</summary>
    [Fact]
    public async Task Copying_a_file_onto_itself_is_refused()
    {
        using var fx = new EngineFixture();
        fx.Write("one.md", "the content\n");

        Assert.False((await fx.Invoke(new CopyFileTool(), """{"from":"one.md","to":"one.md"}""")).Success);
    }

    /// <summary>A missing source is reported, not invented as an empty file.</summary>
    [Fact]
    public async Task A_source_that_is_not_there_fails()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new CopyFileTool(), """{"from":"nothing.md","to":"copy.md"}""");

        Assert.False(result.Success);
        Assert.False(fx.Exists("copy.md"));
    }

    /// <summary>
    /// Journalled, therefore revertible. The reason this goes through the artifact store rather
    /// than File.Copy: a step a reviewer rejects has to be able to take its copy back, which is
    /// exactly what the shell version of this could never do.
    /// </summary>
    [Fact]
    public async Task A_rejected_step_can_take_the_copy_back()
    {
        using var fx = new EngineFixture();
        fx.Write("one.md", "the content\n");

        var step = fx.Artifacts.BeginStep();
        Assert.True((await fx.Invoke(new CopyFileTool(), """{"from":"one.md","to":"two.md"}""", step)).Success);

        await step.RevertAsync(["two.md"], default);

        Assert.False(fx.Exists("two.md"));
        Assert.True(fx.Exists("one.md"));
    }
}
