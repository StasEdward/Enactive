namespace Enactive.Core.Artifacts;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// What the workspace looked like at one moment, so that what a STEP changed can be said afterwards
/// - whichever tool changed it.
///
/// <para><b>Why the store is not enough.</b> The artifact store journals what the file tools write,
/// and until 2026-09-24 that journal was all a reviewer was shown. Everything written by a command
/// was invisible to it: a refactoring by <c>dotnet format</c> or a script, a disk report written with
/// <c>&gt; report.txt</c>, a report appended with PowerShell's <c>Add-Content</c>. And what it was
/// shown of a store-written file was the file's first 8,000 characters - so a step that appended its
/// findings to a long report was judged on a part that could not contain them (run 5e5b51: "the
/// pages 4-6 findings the agent says it appended fall in the part not shown" - PASS).</para>
///
/// <para><b>Two ways to take one.</b> In a git work tree, the whole tree is written into a PRIVATE
/// index file in the system temp folder (<c>GIT_INDEX_FILE</c>, <c>git add -A</c>, <c>git write-tree</c>):
/// the person's own index, branches and stash are never touched, and the only trace in the repository
/// is object files, which <c>git gc</c> reclaims. Comparing two such trees is a real diff of every
/// change, made by anything. Measured on this repository (660 files): 389 ms for the first snapshot,
/// 106 ms after it - git re-hashes only what changed - and 70 ms to compare two. Outside git, the size
/// and write time of each file: that says WHICH files changed, not how.</para>
///
/// <para>Everything here fails soft. A snapshot that cannot be taken is <c>null</c>, and the caller
/// keeps doing what it did before this existed.</para>
/// </summary>
public sealed class WorkspaceChanges : IDisposable
{
    /// <summary>The engine's own folder: runs, memory, the worker's scratch area. Never the work.</summary>
    private const string Own = ".enactive";

    /// <summary>Folders a scan outside git never descends into - the same idea as the census's "build output not counted".</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", Own, "bin", "obj", "node_modules", ".vs", ".idea" };

    /// <summary>One file's diff is kept to this much, whatever git produced; the reviewer cuts further and says so.</summary>
    private const int MaxDiffCharsKept = 200_000;

    private readonly string _root;
    private readonly string _index = Path.Combine(Path.GetTempPath(), $"enactive-snapshot-{Guid.NewGuid():N}.index");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _git;

    public WorkspaceChanges(string root) => _root = root;

    /// <summary>The workspace as it is now, or null when it cannot be said.</summary>
    public async Task<WorkspaceSnapshot?> TakeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _git ??= (await GitAsync(ct, "rev-parse", "--is-inside-work-tree")).Output.Trim() == "true";

            if (_git == true)
            {
                if (!File.Exists(_index) && (await GitAsync(ct, "rev-parse", "--verify", "--quiet", "HEAD")).Exit == 0)
                    await GitAsync(ct, "read-tree", "HEAD");

                var add = await GitAsync(ct, "-c", "core.safecrlf=false", "add", "-A", "--", ".", $":(exclude){Own}");
                if (add.Exit != 0)
                    return null;

                var tree = await GitAsync(ct, "write-tree");
                return tree.Exit == 0 && tree.Output.Trim() is { Length: > 0 } id ? new WorkspaceSnapshot(id, null) : null;
            }

            return Scan() is { } files ? new WorkspaceSnapshot(null, files) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What changed between two snapshots, or null when they cannot be compared.</summary>
    public async Task<IReadOnlyList<FileChange>?> CompareAsync(
        WorkspaceSnapshot before, WorkspaceSnapshot after, CancellationToken ct)
    {
        try
        {
            if (before.Tree is { } from && after.Tree is { } to)
                return from == to ? Array.Empty<FileChange>() : await DiffTreesAsync(from, to, ct);

            if (before.Files is { } was && after.Files is { } now)
                return CompareScans(was, now);

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<FileChange>?> DiffTreesAsync(string from, string to, CancellationToken ct)
    {
        // The list first, NUL-separated: a path with a space or a non-ASCII letter must not be split.
        var names = await GitAsync(ct, "-c", "core.quotepath=false", "diff", "--relative", "-M", "--name-status", "-z", from, to);
        if (names.Exit != 0)
            return null;

        var entries = new List<(char Status, string Path, string? OldPath)>();
        var parts = names.Output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;

            var status = parts[i][0];
            if (status is 'R' or 'C')
            {
                if (i + 2 >= parts.Length) break;
                entries.Add((status, parts[i + 2], parts[i + 1]));
                i += 2;
            }
            else
            {
                if (i + 1 >= parts.Length) break;
                entries.Add((status, parts[i + 1], null));
                i += 1;
            }
        }

        // Then the patch, in ONE call, cut at each file's header. git lists files in the same order in
        // both outputs; a mismatch means the cut cannot be trusted, and the changes go without diffs
        // rather than with the wrong ones.
        var patch = await GitAsync(ct, "-c", "core.quotepath=false", "diff", "--relative", "-M", "-U3", "--no-color", from, to);
        var chunks = patch.Exit == 0 ? SplitPatch(patch.Output) : new List<string>();
        var aligned = chunks.Count == entries.Count;

        var changes = new List<FileChange>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var (status, path, old) = entries[i];
            var diff = aligned ? chunks[i] : null;
            var binary = diff is not null && diff.Contains("\nBinary files ", StringComparison.Ordinal);

            changes.Add(new FileChange(
                path,
                status switch
                {
                    'A' => FileChangeKind.Added,
                    'D' => FileChangeKind.Deleted,
                    'R' => FileChangeKind.Renamed,
                    _ => FileChangeKind.Modified
                },
                binary || diff is null ? null : Kept(diff),
                binary,
                old));
        }

        return changes;
    }

    private static List<string> SplitPatch(string patch)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }

        if (current.Length > 0 && current.ToString().StartsWith("diff --git ", StringComparison.Ordinal))
            chunks.Add(current.ToString());

        return chunks;
    }

    private static string Kept(string diff)
        => diff.Length <= MaxDiffCharsKept ? diff : diff[..MaxDiffCharsKept] + "\n… (diff cut here)";

    /// <summary>
    /// Files up to this size are fingerprinted by their CONTENT outside git, not only by size and
    /// write time - see <see cref="Fingerprint"/>.
    /// </summary>
    private const long MaxHashedFileBytes = 1024 * 1024;

    /// <summary>
    /// How much one snapshot reads to fingerprint files, in all. Past it the rest are known by size and
    /// write time, as before: a folder of media is not worth reading to say whether it changed.
    /// </summary>
    private const long MaxHashedBytesPerSnapshot = 64L * 1024 * 1024;

    private IReadOnlyDictionary<string, (long Size, long Ticks, string? Hash)>? Scan()
    {
        var files = new Dictionary<string, (long, long, string?)>(StringComparer.OrdinalIgnoreCase);
        var hashed = 0L;
        var pending = new Stack<string>();
        pending.Push(_root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!Skipped.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (files.Count >= Enactive.Core.Context.WorkspaceCensus.MaxFilesScanned)
                    return null;   // too big to call "the workspace" honestly

                var info = new FileInfo(file);
                string? hash = null;
                if (info.Length <= MaxHashedFileBytes && hashed + info.Length <= MaxHashedBytesPerSnapshot)
                {
                    hash = Fingerprint(file);
                    hashed += info.Length;
                }
                files[Path.GetRelativePath(_root, file).Replace('\\', '/')] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
            }
        }

        return files;
    }

    /// <summary>
    /// A file's content, as a hash - or null when it cannot be read, and then size and write time
    /// decide.
    ///
    /// <para>Measured 2026-09-24 21:49, run bc3200, in a folder with no git: a step broke
    /// <c>MonitorClient.cs</c> on purpose to check its tests failed, and put it back - 34 edits, and
    /// the file ended byte for byte as it began. By size and write time it had CHANGED, which is the
    /// one thing it had not done.</para>
    /// </summary>
    private static string? Fingerprint(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IReadOnlyList<FileChange> CompareScans(
        IReadOnlyDictionary<string, (long Size, long Ticks, string? Hash)> was,
        IReadOnlyDictionary<string, (long Size, long Ticks, string? Hash)> now)
    {
        var changes = new List<FileChange>();

        foreach (var (path, stamp) in now)
            if (!was.TryGetValue(path, out var before))
                changes.Add(new FileChange(path, FileChangeKind.Added, null, false));
            else if (Differs(before, stamp))
                changes.Add(new FileChange(path, FileChangeKind.Modified, null, false));

        foreach (var path in was.Keys)
            if (!now.ContainsKey(path))
                changes.Add(new FileChange(path, FileChangeKind.Deleted, null, false));

        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return changes;

        // The content decides when both sides have it; a new write time over the same bytes is a file
        // written back as it was, not a change.
        static bool Differs((long Size, long Ticks, string? Hash) before, (long Size, long Ticks, string? Hash) after)
            => before.Size != after.Size
               || (before.Hash is { } a && after.Hash is { } b ? a != b : before.Ticks != after.Ticks);
    }

    private async Task<(int Exit, string Output)> GitAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_INDEX_FILE"] = _index;
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return (-1, "");
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);   // drained so a chatty git cannot block
        await process.WaitForExitAsync(ct);
        await errors;
        return (process.ExitCode, await output);
    }

    public void Dispose()
    {
        try { File.Delete(_index); } catch { /* a temp file; the OS cleans the folder */ }
        _gate.Dispose();
    }
}

/// <summary>One moment of the workspace: a git tree id, or the size, write time and (up to a limit) content hash of every file.</summary>
public sealed record WorkspaceSnapshot(string? Tree, IReadOnlyDictionary<string, (long Size, long Ticks, string? Hash)>? Files);

public enum FileChangeKind { Added, Modified, Deleted, Renamed }

/// <summary>
/// One file a step changed. <see cref="Diff"/> is a unified diff when one could be made - always in a
/// git work tree for a text file, never outside git - and null otherwise.
/// </summary>
public sealed record FileChange(string Path, FileChangeKind Kind, string? Diff, bool Binary, string? OldPath = null);
