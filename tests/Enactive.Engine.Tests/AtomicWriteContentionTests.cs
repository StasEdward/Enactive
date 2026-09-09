namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Somebody else has the file open for a moment. On Windows somebody always does.
///
/// <para><c>ParallelStepTests.Two_branches_writing_one_file_do_not_collide_on_disk</c> failed
/// intermittently - on a developer's machine and then on a CI runner - with "Access to the path is
/// denied" from a step that had done nothing wrong. It was written for a race between two of OUR
/// writers, and that race is closed: <c>DiskArtifactStore</c> holds one lock per path across the
/// whole of a write. This is a different one, and no lock of ours can close it.</para>
///
/// <para><c>File.Move(temp, target, overwrite: true)</c> has to replace the target, and it fails if
/// anything holds a handle to it. Something does: a virus scanner opens a file to scan it the
/// moment it is closed, a search indexer opens it a little later, and a backup agent will do it at
/// the worst possible time. None of them keep it for long. The test above writes one file ten times
/// in a few milliseconds, which is as close to a worst case as ordinary work gets.</para>
///
/// <para>So the move waits and tries again, briefly and a bounded number of times. Retrying the
/// MOVE is safe in a way that retrying the write would not be: the temp file is complete and the
/// target is untouched, so the operation is exactly where it was. What it must not do is retry
/// forever, or turn a genuine permission problem into a hang - after the budget the original
/// exception is thrown, unchanged.</para>
///
/// <para>These tests hold the handle deliberately rather than hoping a scanner turns up, so they
/// fail every time without the retry instead of one run in twenty.</para>
/// </summary>
public sealed class AtomicWriteContentionTests : IDisposable
{
    /// <summary>Long enough that no plausible single attempt gets past it, short enough to sit in a
    /// unit test - and well inside the retry budget, so a pass is the retry working and not luck.</summary>
    private static readonly TimeSpan Held = TimeSpan.FromMilliseconds(200);

    private readonly string _root;

    public AtomicWriteContentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a held file is not a test failure */ }
    }

    /// <summary>The overload the artifact store uses, which is the one the flake came through.</summary>
    [Fact]
    public async Task An_async_replace_waits_out_a_handle_held_on_the_target()
    {
        var path = await HeldFileAsync();

        await AtomicWrite.Replace(path, async stream =>
        {
            var bytes = Encoding.UTF8.GetBytes("after");
            await stream.WriteAsync(bytes);
        });

        Assert.Equal("after", await File.ReadAllTextAsync(path));
    }

    /// <summary>And the synchronous one, which the templates editor and Apply use.</summary>
    [Fact]
    public async Task A_synchronous_replace_waits_out_a_handle_held_on_the_target()
    {
        var path = await HeldFileAsync();

        AtomicWrite.Replace(path, "after");

        Assert.Equal("after", await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// A file that exists, with an exclusive handle on it that is released shortly. The method
    /// returns only once the handle is actually open, so the replace under test always meets it.
    /// </summary>
    private async Task<string> HeldFileAsync()
    {
        var path = Path.Combine(_root, "held.txt");
        await File.WriteAllTextAsync(path, "before");

        var opened = new TaskCompletionSource();

        _ = Task.Run(() =>
        {
            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            opened.SetResult();
            Thread.Sleep(Held);
        });

        await opened.Task;
        return path;
    }
}
