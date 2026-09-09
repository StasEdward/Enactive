namespace Enactive.Workspace;

using System.Text;

/// <summary>
/// Replaces a file's content without ever leaving the target in a half-written state.
///
/// Two failures this exists to stop, both found by the 2026-09-06 follow-up review:
///
/// 1. <b>A failed write used to destroy the original.</b> Opening the target with
///    <see cref="FileMode.Create"/> truncates it to zero bytes BEFORE anything is written, so a
///    provider that threw, a cancellation, or a full disk left the user with an empty or partial
///    file — and, because the journal entry was only added after a successful write, undo did not
///    even know the file had been touched.
/// 2. <b>A fixed temp name destroyed a neighbour.</b> The temp path was always
///    <c>&lt;target&gt;.enactive-tmp</c>, written with a plain overwrite. A user who happened to
///    have a file by that name lost it: applying a change to <c>doc.txt</c> silently consumed
///    <c>doc.txt.enactive-tmp</c>.
///
/// So: a uniquely named temp, created with <see cref="FileMode.CreateNew"/> — which fails rather
/// than overwriting anything that already exists — filled completely, and only then moved over the
/// target. If any step throws, the temp is removed and the target is exactly as it was.
/// </summary>
public static class AtomicWrite
{
    /// <summary>
    /// The synchronous overload, and it is synchronous ALL THE WAY DOWN. It used to call the async
    /// one and block on <c>GetAwaiter().GetResult()</c>, which is the classic sync-over-async
    /// deadlock and it was a real one, not a theoretical one.
    ///
    /// <para>On a thread with a SynchronizationContext — which is to say the UI thread —
    /// <c>FileStream.DisposeAsync</c> flushes buffered bytes, and a flush that does not complete
    /// synchronously posts its continuation back to that context. The context is the dispatcher.
    /// The dispatcher is blocked inside <c>GetResult()</c> waiting for that very continuation.
    /// Nothing moves again: pressing Save hangs the application, with no exception and nothing in
    /// the log. It surfaced the day a settings window first saved a file, and the Apply button on a
    /// staged change has been one unlucky flush away from the same thing for as long as it has
    /// existed.</para>
    ///
    /// <para>The lesson is not "add ConfigureAwait" — that hides this instance and leaves the shape.
    /// A synchronous caller gets a synchronous implementation.</para>
    /// </summary>
    public static void Replace(string fullPath, string content)
    {
        var temp = ReserveTemp(fullPath, out var stream);

        try
        {
            using (stream)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }

            MoveIntoPlace(temp, fullPath);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* nothing left to try */ }
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> against a temp file and moves it over <paramref name="fullPath"/>
    /// only if it completes. The target is untouched until that move.
    /// </summary>
    public static async Task Replace(string fullPath, Func<Stream, Task> write)
    {
        var temp = ReserveTemp(fullPath, out var stream);

        try
        {
            // ConfigureAwait(false) throughout: this is library code with no reason to resume on
            // anybody's UI thread, and a caller that blocks on this Task must not be able to
            // deadlock itself against it. The synchronous overload above no longer does that, but
            // this is the property that makes the next such caller safe too.
            await using (stream.ConfigureAwait(false))
                await write(stream).ConfigureAwait(false);

            await MoveIntoPlaceAsync(temp, fullPath).ConfigureAwait(false);
        }
        catch
        {
            // Clean up our own temp and nothing else. Whatever went wrong, the user's file is
            // still whatever it was before this call.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* nothing left to try */ }
            throw;
        }
    }

    /// <summary>
    /// The last step, with a short budget for whoever else has the target open.
    ///
    /// <para>Replacing a file needs the target, and on Windows something holds it: a virus scanner
    /// opens a file to scan it the moment it is closed, a search indexer opens it a little later, a
    /// backup agent picks its own moment. None of them keep it for long, and while they have it the
    /// move fails with "Access to the path is denied" - a Win32 message that reached the model as a
    /// failed tool call and the step as Incomplete, for nothing the run did wrong.</para>
    ///
    /// <para>That is not the race <c>DiskArtifactStore</c>'s per-path lock closed. That one was two
    /// of OUR writers on one path, and it is closed. This one is somebody else's handle, and no
    /// lock of ours can reach it. It showed up as a test that failed about one run in twenty, first
    /// on a developer's machine and then on CI.</para>
    ///
    /// <para>Retrying the MOVE is safe in a way that retrying the write would not be: the temp file
    /// is complete and the target is untouched, so a failed attempt leaves the operation exactly
    /// where it started. What this must not do is retry forever, or turn a genuine permission
    /// problem into a hang - so the budget is about six tenths of a second and then the original
    /// exception goes on its way, unchanged.</para>
    ///
    /// <para>The synchronous version sleeps, and on the UI thread that is a stall of up to that
    /// budget while a scanner lets go. It is a stall and not a deadlock - nothing is waiting on a
    /// continuation this thread owes itself, which is the trap the rest of this file exists to
    /// avoid - and a save that pauses briefly is a better answer than a save that fails.</para>
    /// </summary>
    private static void MoveIntoPlace(string temp, string fullPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, fullPath, overwrite: true);
                return;
            }
            catch (Exception error) when (IsTransient(error) && attempt < MoveAttempts)
            {
                Thread.Sleep(BackoffMs(attempt));
            }
        }
    }

    /// <inheritdoc cref="MoveIntoPlace"/>
    private static async Task MoveIntoPlaceAsync(string temp, string fullPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, fullPath, overwrite: true);
                return;
            }
            catch (Exception error) when (IsTransient(error) && attempt < MoveAttempts)
            {
                await Task.Delay(BackoffMs(attempt)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Six retries: 10, 20, 40, 80, 160, 320ms — 630ms in total.</summary>
    private const int MoveAttempts = 6;

    private static int BackoffMs(int attempt) => 10 << attempt;

    /// <summary>
    /// Somebody else has it open, or might have. A sharing violation surfaces as either of these
    /// depending on which operation inside the replace hit it, and neither says so in its type.
    ///
    /// <para>"The file is not there" is not that, and waiting 630ms to say so helps nobody.</para>
    /// </summary>
    private static bool IsTransient(Exception error)
        => error is UnauthorizedAccessException
            || (error is IOException and not (FileNotFoundException or DirectoryNotFoundException));

    /// <summary>
    /// Creates a temp file next to the target that did not exist a moment ago. Beside it rather than
    /// in the system temp folder so the final move stays on one volume, which is what makes it
    /// atomic; <see cref="FileMode.CreateNew"/> so a name collision is an error we retry past, never
    /// an existing file we overwrite.
    /// </summary>
    private static string ReserveTemp(string fullPath, out FileStream stream)
    {
        for (var attempt = 0; ; attempt++)
        {
            var candidate = $"{fullPath}.{Guid.NewGuid():N}.enactive-tmp";

            try
            {
                stream = new FileStream(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) && attempt < 5)
            {
                // Astronomically unlikely with a fresh GUID, but "the name was taken" must never
                // become "so I overwrote it".
            }
        }
    }
}
