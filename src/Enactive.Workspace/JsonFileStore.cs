namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Storage;

/// <summary>
/// The read/write half of the JSON-file stores, shared so the two of them cannot drift apart.
///
/// Three things it fixes about how they used to work:
///
/// 1. <b>The lock was per instance.</b> The factories hand a fresh store to each background task, to
///    the UI and to each run, all pointing at the same file. Two of them could read the same list,
///    each append its own entry, and the second write would drop the first — a `SemaphoreSlim` field
///    guards one object, not one file. The gate is now keyed by the file's normalised path.
/// 2. <b>A read error looked like an empty store.</b> A locked or corrupt file returned an empty
///    list through a catch, and the very next save wrote that empty list back over it. A failed
///    read is now reported as a failure, and the caller declines to write rather than replacing
///    content it could not see.
/// 3. <b>Writes were not atomic.</b> <c>File.Create</c> truncates first, so a crash mid-write left
///    invalid JSON — which then read as "empty", which then got saved. Writes go to a temp file and
///    are moved into place.
///
/// 4. <b>The lock stopped at the process boundary.</b> That was a stated limitation — "one desktop
///    app with several tasks inside it" — and the scheduler ended it: the runner the schedule wakes
///    is a SECOND process writing the same files as the open window. MEASURED on 2026-09-10 with
///    two processes appending 200 inbox items each: 200 arrived, 400 were reported written, and
///    neither process saw an error. The read-modify-write now holds a lock FILE beside the target,
///    so the critical section is one per machine rather than one per process.
///
/// The lock itself is <see cref="FileLock"/>, in Core, because the schedules file needs the same one
/// and is written from there. It was here first and moved when the second caller appeared; a lock
/// implemented twice is two locks, which is no lock at all.
///
/// SQLite and MySQL remain the answer for anything heavier — a shared store, or writers that are not
/// this machine. This makes the file stores correct for two local processes, not distributed.
/// </summary>
internal static class JsonFileStore
{
    /// <summary>
    /// Takes the file's critical section - against the other tasks in this process AND against the
    /// other processes on this machine - and gives it back when disposed.
    ///
    /// <para>Every read-modify-write goes through this. A read on its own does not need it, but a
    /// read that is going to be written back does: that is the pair the lock exists for.</para>
    /// </summary>
    /// <exception cref="TimeoutException">
    /// Another process has held the file for <see cref="FileLock.Wait"/>. Thrown rather than
    /// returned, because the alternative is writing anyway - the exact behaviour being removed.
    /// </exception>
    public static Task<FileLock.Hold> HoldAsync(string path, CancellationToken ct)
        => FileLock.TakeAsync(path, ct);

    /// <summary>
    /// Reads the list. <c>Readable</c> is false when the file exists but could not be understood —
    /// which is NOT the same as an empty store, and must never be treated as one.
    /// </summary>
    public static async Task<(List<T> Items, bool Readable)> LoadAsync<T>(
        string path, JsonSerializerOptions options, CancellationToken ct)
    {
        if (!File.Exists(path))
            return (new List<T>(), true);

        try
        {
            await using var stream = File.OpenRead(path);
            var list = await JsonSerializer.DeserializeAsync<List<T>>(stream, options, ct).ConfigureAwait(false);
            return (list ?? new List<T>(), true);
        }
        catch
        {
            return (new List<T>(), false);
        }
    }

    /// <summary>
    /// Writes the list atomically: a temp file beside the target, then a move. An interrupted save
    /// leaves the previous file intact instead of a half-written one.
    /// </summary>
    public static async Task SaveAsync<T>(
        string path, List<T> items, JsonSerializerOptions options, CancellationToken ct)
    {
        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, items, options, ct).ConfigureAwait(false);

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Moves a file that could not be parsed out of the way, so the next write has somewhere to go
    /// and the damaged content is still there to look at. Returns whether the current content was
    /// actually preserved somewhere; the caller must not write a fresh file over it when this is
    /// false, or the damaged content it could not save is simply gone.
    ///
    /// <para><b>Each corruption gets its OWN backup.</b> This used to stop at the first one - "one
    /// copy is enough; do not overwrite the first failure" - and left it at that whether or not a
    /// second corruption came after it. Reported 2026-09-24
    ///: a store corrupted, quarantined,
    /// written fresh, corrupted a SECOND time - `QuarantineUnreadable` saw the first backup already
    /// there, did nothing, and the caller saved over the second corruption anyway. Whatever was
    /// recoverable in it is gone, with no trace it ever existed.</para>
    /// </summary>
    public static bool QuarantineUnreadable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;   // nothing there to have preserved

            var broken = path + ".unreadable";
            for (var n = 2; File.Exists(broken); n++)
                broken = path + $".unreadable.{n}";

            File.Move(path, broken);
            return true;
        }
        catch
        {
            return false;   // nothing here is worth failing a run over, but the caller must know
        }
    }
}
