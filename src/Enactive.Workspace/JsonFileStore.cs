namespace Enactive.Workspace;

using System.Collections.Concurrent;
using System.Text.Json;

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
/// Cross-process safety is still out of scope: this is one desktop app with several tasks inside it.
/// Concurrent runs against a shared store belong on SQLite or MySQL, which is what the factories are
/// for.
/// </summary>
internal static class JsonFileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The lock for one FILE, shared by every store instance addressing it.</summary>
    public static SemaphoreSlim GateFor(string path)
        => Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

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
    /// Moves a file that could not be parsed out of the way, once, so the next write has somewhere to
    /// go and the damaged content is still there to look at. Best-effort by design.
    /// </summary>
    public static void QuarantineUnreadable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var broken = path + ".unreadable";
            if (File.Exists(broken))
                return;   // one copy is enough; do not overwrite the first failure

            File.Move(path, broken);
        }
        catch { /* nothing here is worth failing a run over */ }
    }
}
