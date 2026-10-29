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

    /// <summary>
    /// A command that changed the repository's own settings: the blob was made under the old ones, and read under the new
    /// ones the check would answer for settings that never made it (review of PR #65: core.autocrlf set false passed).
    /// </summary>
    [Fact]
    public async Task A_command_that_changed_the_repository_s_settings_puts_nothing_back()
    {
        var (result, changes) = await AroundAsync(() =>
        {
            File.WriteAllText(Full("prices.bin"), "rewritten");
            TestGit.Run(_root, "config", "core.autocrlf", "false");
        }, beforeSnapshots: () => TestGit.Run(_root, "config", "core.autocrlf", "true"));
        using (changes)
        {
            Assert.Equal(PutBack.Left(PutBackReasons.Settings), result);
            Assert.Equal("rewritten", File.ReadAllText(Full("prices.bin")));
        }
    }

    /// <summary>
    /// The file is the file again, not a new one: a ReadOnly file stays ReadOnly - before, the move over it was refused and
    /// it was reported as not put back exactly - and nothing of the put-back is left in the project, only in the engine's
    /// own folder, which it empties (review of PR #65: a crash left ".prices.bin.enactive-putback" beside the file).
    /// </summary>
    [Fact]
    public async Task A_read_only_file_is_put_back_read_only_and_nothing_is_left_beside_it()
    {
        var (result, changes) = await AroundAsync(() =>
        {
            File.SetAttributes(Full("prices.bin"), FileAttributes.Normal);
            File.WriteAllText(Full("prices.bin"), "rewritten");
            File.SetAttributes(Full("prices.bin"), FileAttributes.ReadOnly);
        });
        using (changes)
        {
            Assert.Equal(PutBack.Back, result);
            Assert.Equal(Original, File.ReadAllBytes(Full("prices.bin")));
            if (OperatingSystem.IsWindows())
                Assert.True(File.GetAttributes(Full("prices.bin")).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(["prices.bin"], Directory.GetFiles(_root).Select(Path.GetFileName));
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".enactive", "putback")));
        }
    }

    /// <summary>Outside Windows, an executable stays executable, as the snapshot recorded it (review of PR #65).</summary>
    [Fact]
    public async Task An_executable_is_put_back_executable()
    {
        if (OperatingSystem.IsWindows()) return;   // no exec bit to keep
        var (result, changes) = await AroundAsync(() => File.WriteAllText(Full("prices.bin"), "rewritten"), beforeSnapshots: () =>
        {
            File.SetUnixFileMode(Full("prices.bin"), File.GetUnixFileMode(Full("prices.bin")) | UnixFileMode.UserExecute);
            TestGit.Commit(_root);
        });
        using (changes)
        {
            Assert.Equal(PutBack.Back, result);
            Assert.True(File.GetUnixFileMode(Full("prices.bin")).HasFlag(UnixFileMode.UserExecute));
        }
    }
}
