namespace Enactive.Workspace;

using System.Collections.Concurrent;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;

/// <summary>
/// What one write did to one path, kept so it can be undone. Recorded at write time because
/// afterwards none of it is knowable: the file exists either way, and its previous bytes are gone.
/// </summary>
public sealed record FileWriteRecord(
    string RelativePath,
    bool ExistedBefore,
    string? BeforeHash,
    string? BackupPath,
    string AfterHash);

/// <summary>Why an undo could not be performed, or that it was.</summary>
public sealed record UndoResult(bool Undone, string? Conflict = null, bool Restored = false)
{
    public static UndoResult Deleted => new(true);
    public static UndoResult RestoredPrevious => new(true, Restored: true);
    public static UndoResult Blocked(string reason) => new(false, reason);
}

/// <summary>
/// Disk-backed artifact store rooted at the workspace. The rest of the app addresses artifacts by
/// id / <see cref="ArtifactRef"/> and never learns the physical path (PLAN_v2 §2A.4).
///
/// It also keeps what it overwrote. "Undo" used to be an unconditional <c>File.Delete</c>: if the
/// agent edited an existing source file, undoing the edit deleted the source and reported "undone".
/// Now the previous bytes are copied into the workspace's own state folder first, so undo either
/// restores the old version or removes a file this run created — and refuses either when the file
/// has changed since, because at that point undoing would destroy someone else's work.
/// </summary>
public sealed class DiskArtifactStore : IArtifactStore
{
    private readonly string _root;
    private readonly string _backupRoot;
    private readonly ConcurrentDictionary<Guid, string> _paths = new();
    private readonly ConcurrentDictionary<string, FileWriteRecord> _writes =
        new(StringComparer.OrdinalIgnoreCase);
    private int _backupSequence;

    public DiskArtifactStore(WorkspaceInfo workspace, Guid runId = default)
    {
        _root = Path.GetFullPath(workspace.RootPath);
        _backupRoot = Path.Combine(
            _root, WorkspaceGuard.ReservedFolder, "undo",
            (runId == Guid.Empty ? Guid.NewGuid() : runId).ToString("N"));
    }

    public string Root => _root;

    /// <summary>Every path this store wrote, with what it needs to undo the write.</summary>
    public IReadOnlyCollection<FileWriteRecord> Writes => _writes.Values.ToArray();

    /// <summary>
    /// True when this store created the file rather than overwriting an existing one. False for a
    /// path it never wrote, so the caller can only ever be told LESS than it is safe to delete.
    /// </summary>
    public bool CreatedHere(string relativePath)
        => _writes.TryGetValue(relativePath, out var record) && !record.ExistedBefore;

    public FileWriteRecord? WriteFor(string relativePath)
        => _writes.TryGetValue(relativePath, out var record) ? record : null;

    public async Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
    {
        var fullPath = ResolveInsideRoot(relativePath);

        var existed = File.Exists(fullPath);
        var beforeHash = existed ? FileHash.OfFile(fullPath) : null;
        var backupPath = existed ? BackUp(fullPath) : null;

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await write(stream);

        // Only the FIRST write to a path records what was there before this run touched it; a later
        // write in the same run overwrites the store's own output, which does not make a pre-existing
        // file ours to delete, and must not replace the backup of the user's version.
        _writes.AddOrUpdate(
            relativePath,
            _ => new FileWriteRecord(
                relativePath, existed, beforeHash, backupPath, FileHash.OfFile(fullPath)!),
            (_, first) => first with { AfterHash = FileHash.OfFile(fullPath)! });

        var id = Guid.NewGuid();
        _paths[id] = fullPath;
        return new ArtifactRef(id, kind, title, relativePath);
    }

    /// <summary>
    /// Undoes this store's write to a path. A file that existed before is restored from its backup;
    /// one this run created is deleted. Either way it happens ONLY when the file still holds exactly
    /// what the run left there — otherwise undoing would throw away an edit made since.
    /// </summary>
    public UndoResult Undo(string relativePath)
    {
        if (!_writes.TryGetValue(relativePath, out var record))
            return UndoResult.Blocked("This run did not write that file.");

        string fullPath;
        try { fullPath = ResolveInsideRoot(relativePath); }
        catch (Exception ex) { return UndoResult.Blocked(ex.Message); }

        var currentHash = FileHash.OfFile(fullPath);
        if (currentHash is null)
            return UndoResult.Blocked("The file is already gone.");

        if (!string.Equals(currentHash, record.AfterHash, StringComparison.Ordinal))
            return UndoResult.Blocked(
                "The file has changed since the run wrote it — undoing now would discard that edit.");

        try
        {
            if (!record.ExistedBefore)
            {
                File.Delete(fullPath);
                _writes.TryRemove(relativePath, out _);
                return UndoResult.Deleted;
            }

            if (record.BackupPath is null || !File.Exists(record.BackupPath))
                return UndoResult.Blocked("The previous version is no longer available.");

            File.Copy(record.BackupPath, fullPath, overwrite: true);
            _writes.TryRemove(relativePath, out _);
            return UndoResult.RestoredPrevious;
        }
        catch (Exception ex)
        {
            return UndoResult.Blocked(ex.Message);
        }
    }

    public Task<Stream> OpenAsync(Guid artifactId, CancellationToken ct)
    {
        if (!_paths.TryGetValue(artifactId, out var path))
            throw new FileNotFoundException("Unknown artifact id.", artifactId.ToString());

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid artifactId, CancellationToken ct)
    {
        if (_paths.TryRemove(artifactId, out var path) && File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies the current file into the workspace's state folder and returns where it went, or null
    /// if that could not be done — in which case the write still goes ahead and undo will say it has
    /// no previous version, rather than pretending it has one.
    /// </summary>
    private string? BackUp(string fullPath)
    {
        try
        {
            Directory.CreateDirectory(_backupRoot);
            PruneOldBackups();
            var sequence = Interlocked.Increment(ref _backupSequence);
            var backup = Path.Combine(_backupRoot, $"{sequence:D4}{Path.GetExtension(fullPath)}.bak");
            File.Copy(fullPath, backup, overwrite: true);
            return backup;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the most recent runs' backups and drops the rest. Undo only ever reaches back into the
    /// CURRENT run, so older copies are there for a person digging something out by hand — worth
    /// keeping a few of, not worth growing without limit inside the user's project.
    /// </summary>
    private void PruneOldBackups()
    {
        const int Keep = 20;

        try
        {
            var parent = Path.GetDirectoryName(_backupRoot);
            if (parent is null || !Directory.Exists(parent))
                return;

            var stale = new DirectoryInfo(parent)
                .GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Skip(Keep);

            foreach (var directory in stale)
                try { directory.Delete(recursive: true); } catch { /* in use, or gone already */ }
        }
        catch { /* housekeeping must never break a write */ }
    }

    /// <summary>
    /// One shared rule (<see cref="WorkspaceGuard"/>) instead of this store's own copy of it. The
    /// copy compared strings only, so a junction inside the workspace passed the check and the write
    /// then landed wherever the junction pointed.
    /// </summary>
    private string ResolveInsideRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is empty.", nameof(relativePath));

        return WorkspaceGuard.ResolveInside(_root, relativePath);
    }
}
