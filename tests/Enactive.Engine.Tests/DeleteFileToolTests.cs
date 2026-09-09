namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// Deleting a file — the one file tool that names something no other tool could do.
///
/// <para>Everything else here replaces a bad way of doing something: a copy that came out partial,
/// a rename that went through a shell. This adds a capability. So the tests that matter are the
/// ones about what it refuses.</para>
/// </summary>
public sealed class DeleteFileToolTests
{
    /// <summary>
    /// It always asks, at every tier including Autonomous. Every other file tool leaves something a
    /// person can look at and judge; this one leaves an absence, and an absence is the hardest
    /// thing to notice afterwards.
    /// </summary>
    [Fact]
    public void It_always_asks_first()
        => Assert.True(new DeleteFileTool().RequiresApproval);

    [Fact]
    public async Task It_deletes_the_file()
    {
        using var fx = new EngineFixture();
        fx.Write("gone.md", "the content\n");

        var result = await fx.Invoke(new DeleteFileTool(), """{"path":"gone.md"}""");

        Assert.True(result.Success, result.Error);
        Assert.False(fx.Exists("gone.md"));
    }

    /// <summary>
    /// Journalled, so a rejected step puts the file back — contents and all. This is the whole
    /// reason it goes through the store rather than calling File.Delete: a deletion that cannot be
    /// undone is the shell's version of this tool with a nicer name.
    /// </summary>
    [Fact]
    public async Task A_rejected_step_puts_the_file_back()
    {
        using var fx = new EngineFixture();
        fx.Write("gone.md", "the content\n");

        var step = fx.Artifacts.BeginStep();
        Assert.True((await fx.Invoke(new DeleteFileTool(), """{"path":"gone.md"}""", step)).Success);
        Assert.False(fx.Exists("gone.md"));

        await step.RevertAsync(["gone.md"], default);

        Assert.True(fx.Exists("gone.md"));
        Assert.Equal("the content\n", fx.Read("gone.md"));
    }

    /// <summary>
    /// One file, never a tree. "Delete the folder" is a different request with a different blast
    /// radius, and a tool that quietly did both would be answering something nobody asked.
    ///
    /// <para>This asserts WHICH refusal, not that one happened. Without the directory check the
    /// call still fails — File.Exists is false for a folder, so it falls through to "File not
    /// found" — and an earlier version of this test passed on exactly that, which made it a test of
    /// nothing. "Not found" about a folder sitting right there sends the reader hunting for a typo
    /// instead of reading the request they actually made.</para>
    /// </summary>
    [Fact]
    public async Task It_refuses_a_directory_as_a_directory()
    {
        using var fx = new EngineFixture();
        fx.Write("keep/inside.md", "the content\n");

        var result = await fx.Invoke(new DeleteFileTool(), """{"path":"keep"}""");

        Assert.False(result.Success);
        Assert.Contains("directory", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.True(fx.Exists("keep/inside.md"));
    }

    /// <summary>
    /// A file that is not there is reported as that, not shrugged off as success. "Deleted" about a
    /// path that never existed is a report somebody would act on.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_there_fails()
    {
        using var fx = new EngineFixture();

        Assert.False((await fx.Invoke(new DeleteFileTool(), """{"path":"nothing.md"}""")).Success);
    }
}
