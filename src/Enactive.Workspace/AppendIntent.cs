namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Context;

/// <summary>Crash recovery for an in-place append. Only the added bytes are persisted.
/// A live operation keeps the intent exclusively open. Dispose closes it; Complete removes it.</summary>
internal sealed class AppendIntent : IDisposable
{
    private sealed record State(string Path, long Length, string BeforeHash);
    private readonly FileStream _intent;
    private readonly string _tail;
    private readonly string _name;
    private AppendIntent(FileStream intent, string name, string tail) { _intent = intent; _name = name; _tail = tail; }

    internal static async Task<AppendIntent> PrepareAsync(string directory, string path, long length,
        string beforeHash, Stream tail, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var name = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}.append.json");
        var tailName = System.IO.Path.ChangeExtension(name, ".tail");
        var preparing = System.IO.Path.ChangeExtension(name, ".preparing");
        FileStream? intent = null;
        try
        {
            // Mutations can only start after the flushed preparation is renamed to a ready intent.
            // Delete sharing permits that rename while denying other readers/writers throughout.
            intent = new FileStream(preparing, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
            await using (var copy = new FileStream(tailName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await tail.CopyToAsync(copy, ct);
                copy.Flush(flushToDisk: true);
            }
            var state = JsonSerializer.SerializeToUtf8Bytes(new State(path, length, beforeHash));
            await intent.WriteAsync(state, ct);
            intent.Flush(flushToDisk: true);
            File.Move(preparing, name);
            return new AppendIntent(intent, name, tailName);
        }
        catch
        {
            intent?.Dispose();
            try { File.Delete(preparing); File.Delete(name); File.Delete(tailName); } catch { }
            throw;
        }
    }

    internal void Complete()
    {
        var name = _name;
        _intent.Dispose();
        // Removing the intent is the commit point. An orphan tail cannot trigger rollback.
        File.Delete(name);
        try { File.Delete(_tail); } catch (IOException) { }
    }

    public void Dispose() => _intent.Dispose();

    internal static IReadOnlyList<string> Recover(string root)
    {
        var conflicts = new List<string>();
        var undo = System.IO.Path.Combine(root, WorkspaceGuard.ReservedFolder, "undo");
        if (!Directory.Exists(undo)) return conflicts;
        try
        {
            if ((File.GetAttributes(undo) & FileAttributes.ReparsePoint) != 0) return conflicts;
            foreach (var directory in Directory.EnumerateDirectories(undo).Take(10_000))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var preparing in Directory.EnumerateFiles(directory, "*.append.preparing").Take(10_000))
                {
                    try
                    {
                        if (RemoveInactive(preparing))
                            RemoveInactive(System.IO.Path.ChangeExtension(preparing, ".tail"));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                foreach (var name in Directory.EnumerateFiles(directory, "*.append.json").Take(10_000))
                {
                    try { RecoverOne(root, name, conflicts); }
                    catch (IOException) { /* active append or inaccessible file: do not interfere */ }
                    catch (UnauthorizedAccessException) { }
                    catch (JsonException) { conflicts.Add(name); }
                    catch (ArgumentException) { conflicts.Add(name); }
                }
                // A committed append can leave a tail if the process exits between the two deletes.
                foreach (var tail in Directory.EnumerateFiles(directory, "*.append.tail").Take(10_000))
                {
                    // Check in transition order: preparation may become ready between these reads.
                    if (File.Exists(System.IO.Path.ChangeExtension(tail, ".preparing"))
                        || File.Exists(System.IO.Path.ChangeExtension(tail, ".json"))) continue;
                    try { RemoveInactive(tail); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return conflicts;
    }

    private static bool RemoveInactive(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        // An active Prepare still owns its handle: failure to open means leave both files alone.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        File.Delete(path);
        return true;
    }

    private static void RecoverOne(string root, string name, List<string> conflicts)
    {
        if ((File.GetAttributes(name) & FileAttributes.ReparsePoint) != 0) return;
        var tailName = System.IO.Path.ChangeExtension(name, ".tail");
        using (var intent = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (intent.Length == 0 || intent.Length > 8192) { conflicts.Add(name); return; }
            var state = JsonSerializer.Deserialize<State>(intent);
            if (state is null || state.Length < 0 || state.BeforeHash is not { Length: 64 })
            { conflicts.Add(name); return; }
            var full = WorkspaceGuard.ResolveInside(root, state.Path);
            if ((File.GetAttributes(tailName) & FileAttributes.ReparsePoint) != 0) return;
            using var tail = File.OpenRead(tailName);
            using var file = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (file.Length < state.Length || file.Length - state.Length > tail.Length
                || DiskArtifactStore.HashPrefix(file, state.Length) != state.BeforeHash)
            { conflicts.Add(state.Path); return; }
            var actual = new byte[81920];
            var expected = new byte[81920];
            // The prefix and every byte of the possibly partial append must still be ours.
            while (file.Position < file.Length)
            {
                var count = file.Read(actual, 0, actual.Length);
                tail.ReadExactly(expected.AsSpan(0, count));
                if (!actual.AsSpan(0, count).SequenceEqual(expected.AsSpan(0, count)))
                { conflicts.Add(state.Path); return; }
            }
            file.SetLength(state.Length);
            file.Flush(flushToDisk: true);
        }
        File.Delete(name);
        File.Delete(tailName);
    }
}
