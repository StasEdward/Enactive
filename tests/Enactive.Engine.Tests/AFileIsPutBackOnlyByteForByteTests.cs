namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Bench build-error, 2026-10-18: refused twice on edit_file, the worker rewrote the forbidden file with a PowerShell
/// <c>-replace | Set-Content</c>, and nothing could put it back - a command's writes are not the store's. The workspace
/// snapshot around such a command holds the file as git stored it; it is written back only where those are the bytes
/// that were there, and named otherwise: never a file that was not there.
/// </summary>
public sealed class AFileIsPutBackOnlyByteForByteTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("enactive-putback-").FullName;
    private static readonly byte[] Original = [(byte)'a', (byte)'\r', (byte)'\n', (byte)'b', (byte)'\n', 0, 0xff];

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private string Full(string rel) => Path.Combine(_root, rel);

    /// <summary>A committed repository holding prices.bin, a command run between two snapshots, then put back.</summary>
    private async Task<(PutBack Result, WorkspaceChanges Changes)> AroundAsync(Action command, Action? beforeSnapshots = null,
        IReadOnlyCollection<string>? changedWithIt = null, Action? afterSnapshots = null, bool git = true)
    {
        File.WriteAllBytes(Full("prices.bin"), Original);
        if (git) TestGit.Repository(_root);
        beforeSnapshots?.Invoke();
        var changes = new WorkspaceChanges(_root);
        var before = (await changes.TakeAsync(default))!;
        command();
        var after = (await changes.TakeAsync(default))!;
        afterSnapshots?.Invoke();
        return (await changes.PutBackAsync(before, after, "prices.bin", changedWithIt ?? ["prices.bin"], default), changes);
    }

    [Fact]
    public async Task A_file_a_command_rewrote_is_put_back_byte_for_byte()
    {
        var (result, changes) = await AroundAsync(() => File.WriteAllText(Full("prices.bin"), "rewritten"));
        using (changes)
        {
            Assert.Equal(PutBack.Back, result);
            Assert.Equal(Original, File.ReadAllBytes(Full("prices.bin")));
            Assert.Empty(Directory.GetFiles(_root, "*.enactive-putback"));
        }
    }

    [Fact]
    public async Task A_file_a_command_deleted_is_written_back_whole()
    {
        var (result, changes) = await AroundAsync(() => File.Delete(Full("prices.bin")));
        using (changes)
        {
            Assert.True(result.Done, result.Why);
            Assert.Equal(Original, File.ReadAllBytes(Full("prices.bin")));
        }
    }

    [Fact]
    public async Task Where_the_repository_converts_line_endings_nothing_is_written()
    {
        var (result, changes) = await AroundAsync(() => File.WriteAllText(Full("prices.bin"), "rewritten"),
            beforeSnapshots: () => TestGit.Run(_root, "config", "core.autocrlf", "true"));
        using (changes)
        {
            Assert.Equal(PutBack.Left(PutBackReasons.Conversion), result);
            Assert.Equal("rewritten", File.ReadAllText(Full("prices.bin")));
        }
    }

    [Fact]
    public async Task A_text_attribute_is_a_conversion_too()
    {
        var (result, changes) = await AroundAsync(() => File.WriteAllText(Full("prices.bin"), "rewritten"),
            beforeSnapshots: () => { File.WriteAllText(Full(".gitattributes"), "* text=auto\n"); TestGit.Commit(_root); });
        using (changes)
            Assert.Equal(PutBack.Left(PutBackReasons.Conversion), result);
    }

    [Fact]
    public async Task A_command_that_changed_gitattributes_puts_nothing_back()
    {
        var (result, changes) = await AroundAsync(() => File.WriteAllText(Full("prices.bin"), "rewritten"),
            changedWithIt: ["prices.bin", "sub/.gitattributes"]);
        using (changes)
        {
            Assert.Equal(PutBack.Left(PutBackReasons.Attributes), result);
            Assert.Equal("rewritten", File.ReadAllText(Full("prices.bin")));
        }
    }

    [Fact]
    public async Task A_file_changed_again_after_the_command_is_left()
    {
        var (result, changes) = await AroundAsync(() => File.WriteAllText(Full("prices.bin"), "rewritten"),
            afterSnapshots: () => File.WriteAllText(Full("prices.bin"), "a person's edit"));
        using (changes)
        {
            Assert.Equal(PutBack.Left(PutBackReasons.ChangedAgain), result);
            Assert.Equal("a person's edit", File.ReadAllText(Full("prices.bin")));
        }
    }

    [Fact]
    public async Task Outside_git_nothing_is_put_back()
    {
        var (result, changes) = await AroundAsync(() => File.WriteAllText(Full("prices.bin"), "rewritten"), git: false);
        using (changes)
        {
            Assert.Equal(PutBack.Left(PutBackReasons.NotGit), result);
            Assert.Equal("rewritten", File.ReadAllText(Full("prices.bin")));
        }
    }

    [Theory]
    [InlineData("100644", true)]
    [InlineData("100755", true)]
    [InlineData("120000", false)]
    [InlineData("160000", false)]
    public void Only_a_regular_file_mode_is_written(string mode, bool regular)
        => Assert.Equal(regular, ExactRestore.Regular(mode));

    [Fact]
    public async Task Bytes_that_do_not_hash_to_the_blob_are_not_moved_into_place()
    {
        File.WriteAllText(Full("candidate"), "x");
        TestGit.Repository(_root);

        Assert.False(await ExactRestore.VerifiedAsync(_root, Full("candidate"), "0000000000000000000000000000000000000000", default));
    }
}
