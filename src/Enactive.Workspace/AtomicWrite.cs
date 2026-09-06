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
    public static void Replace(string fullPath, string content)
        => Replace(fullPath, stream =>
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    /// <summary>
    /// Runs <paramref name="write"/> against a temp file and moves it over <paramref name="fullPath"/>
    /// only if it completes. The target is untouched until that move.
    /// </summary>
    public static async Task Replace(string fullPath, Func<Stream, Task> write)
    {
        var temp = ReserveTemp(fullPath, out var stream);

        try
        {
            await using (stream)
                await write(stream);

            File.Move(temp, fullPath, overwrite: true);
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
