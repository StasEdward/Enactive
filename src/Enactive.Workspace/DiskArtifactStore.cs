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
    // Null when this entry RECORDS A REMOVAL: the path was deleted, so there is no content to hash.
    string? AfterHash,
    // WHO made this write: the owner of the scope it came through - see DiskArtifactStore.NewOwner.
    // A revert only undoes its OWN writes; one belonging to another owner is a conflict to report,
    // never work to throw away. Recorded from the scope that performed the write, never read off a
    // shared "newest" field, because that field gave two interleaved steps the same number and let
    // one step's revert destroy the other's accepted work.
    int Owner = -1);

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
/// Now the previous bytes are copied into the workspace's own state folder before every write, so a
/// write can be undone to the state before the RUN (the artifact card) or before a STEP
/// (<see cref="RevertOwnedAsync"/>, used when a reviewer rejects the work) — and both refuse when the
/// file has changed since, because at that point undoing would destroy someone else's work.
/// </summary>
public sealed class DiskArtifactStore : IOwnedArtifactStore
{
    private readonly string _root;
    private readonly string _backupRoot;
    private readonly ConcurrentDictionary<Guid, string> _paths = new();

    /// <summary>
    /// Every write, in order. A journal rather than one record per path: reverting to "before this
    /// step" needs the state at an arbitrary point, not just the state before the run, and a second
    /// write to a path must not erase what the first one displaced.
    /// </summary>
    private readonly List<FileWriteRecord> _journal = new();
    private readonly object _journalGate = new();
    private int _backupSequence;

    /// <summary>
    /// A write nobody claimed — made straight through the store rather than through a step's scope.
    /// It belongs to no owner, so no revert will ever roll it back, and it makes every owner's
    /// revert treat it as somebody else's work. That is the safe reading of "we do not know".
    /// </summary>
    private const int Unowned = -1;

    public DiskArtifactStore(WorkspaceInfo workspace, Guid runId = default)
    {
        _root = Path.GetFullPath(workspace.RootPath);
        _backupRoot = Path.Combine(
            _root, WorkspaceGuard.ReservedFolder, "undo",
            (runId == Guid.Empty ? Guid.NewGuid() : runId).ToString("N"));
    }

    public string Root => _root;

    /// <summary>Every write this store made, oldest first.</summary>
    public IReadOnlyList<FileWriteRecord> Writes
    {
        get { lock (_journalGate) return _journal.ToArray(); }
    }

    /// <summary>
    /// True when this store created the file rather than overwriting an existing one — judged by the
    /// FIRST write, since a later write in the same run overwrites this store's own output and does
    /// not make a pre-existing file ours to delete. False for a path it never wrote, so the caller
    /// can only ever be told LESS than it is safe to delete.
    /// </summary>
    public bool CreatedHere(string relativePath)
        => FirstWrite(relativePath) is { ExistedBefore: false };

    /// <summary>
    /// True only when a backup of the displaced version is on disk right now. BackUp is deliberately
    /// best-effort — a write must not fail because housekeeping did — so the answer has to come from
    /// the file, not from the intention.
    /// </summary>
    public bool CanRestore(string relativePath)
        => FirstWrite(relativePath) is { ExistedBefore: true, BackupPath: { } backup }
           && File.Exists(backup);

    /// <summary>The state this path was in before the run first touched it.</summary>
    public FileWriteRecord? FirstWrite(string relativePath)
    {
        lock (_journalGate)
            return _journal.FirstOrDefault(
                w => string.Equals(w.RelativePath, relativePath, WorkspaceGuard.Comparison));
    }

    /// <summary>What the last write to this path left behind.</summary>
    public FileWriteRecord? LastWrite(string relativePath)
    {
        lock (_journalGate)
            return _journal.LastOrDefault(
                w => string.Equals(w.RelativePath, relativePath, WorkspaceGuard.Comparison));
    }

    /// <summary>
    /// A new owner id, recorded against the journal as it stands now. The id identifies WHO is about
    /// to write — not merely when — which is what lets one step's work be undone without touching a
    /// concurrent step's. It is handed to a scope and never read off a shared field again: the field
    /// version gave every write whatever value happened to be newest, so two steps that opened in
    /// one order and wrote in the other both got the second one's number.
    /// </summary>
    public int NewOwner()
    {
        lock (_journalGate)
        {
            _ownerPositions.Add(_journal.Count);
            return _ownerPositions.Count - 1;
        }
    }

    public IArtifactScope BeginStep() => new ArtifactScope(this, NewOwner());

    /// <summary>
    /// Every path this owner wrote or removed, from the journal — the record made when the operation
    /// happened, not a reading of what the model said it would do.
    /// </summary>
    public IReadOnlyCollection<string> TouchedBy(int owner)
    {
        lock (_journalGate)
            return _journal
                .Where(w => w.Owner == owner)
                .Select(w => w.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    /// <summary>Journal position each owner was created at, indexed by owner id.</summary>
    private readonly List<int> _ownerPositions = new();

    /// <summary>A write made outside any step's scope. It is nobody's, and nobody can undo it.</summary>
    public Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
        => CreateAsync(relativePath, kind, title, write, Unowned, ct);

    public async Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write,
        int owner, CancellationToken ct)
    {
        var fullPath = ResolveInsideRoot(relativePath);

        var existed = File.Exists(fullPath);
        var beforeHash = existed ? FileHash.OfFile(fullPath) : null;
        var backupPath = existed ? BackUp(fullPath) : null;

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Built somewhere else and moved into place. Opening the target itself with FileMode.Create
        // emptied it before the first byte arrived, so a provider that threw halfway — or a
        // cancellation, or a full disk — replaced the user's file with a fragment, and nothing was
        // journalled because the entry was only added on success. An exception now propagates with
        // the file exactly as it was.
        await AtomicWrite.Replace(fullPath, write);

        lock (_journalGate)
            _journal.Add(new FileWriteRecord(
                relativePath, existed, beforeHash, backupPath, FileHash.OfFile(fullPath)!, owner));

        var id = Guid.NewGuid();
        _paths[id] = fullPath;
        return new ArtifactRef(id, kind, title, relativePath);
    }

    /// <summary>
    /// Deletes a file and journals the deletion, so a rejected step or an Undo puts it back. The
    /// backup is taken first and the entry is written before the delete, because after the delete
    /// neither is knowable — the same reason a write records what it displaced.
    /// </summary>
    public Task RemoveAsync(string relativePath, CancellationToken ct)
        => RemoveAsync(relativePath, Unowned, ct);

    /// <inheritdoc cref="RemoveAsync(string, CancellationToken)"/>
    public async Task RemoveAsync(string relativePath, int owner, CancellationToken ct)
    {
        var fullPath = ResolveInsideRoot(relativePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"No such file in this workspace: {relativePath}", relativePath);

        var beforeHash = FileHash.OfFile(fullPath);
        var backupPath = BackUp(fullPath);

        if (backupPath is null)
            throw new IOException(
                $"Could not keep a copy of '{relativePath}', so removing it could not be undone. "
                + "Nothing was deleted.");

        lock (_journalGate)
            _journal.Add(new FileWriteRecord(
                relativePath, ExistedBefore: true, beforeHash, backupPath, AfterHash: null, owner));

        File.Delete(fullPath);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Undoes this store's whole effect on a path: a file that existed before the run is restored,
    /// one the run created is deleted. Only when the file still holds exactly what the run left
    /// there — otherwise undoing would throw away an edit made since.
    /// </summary>
    public UndoResult Undo(string relativePath)
    {
        var first = FirstWrite(relativePath);
        var last = LastWrite(relativePath);
        if (first is null || last is null)
            return UndoResult.Blocked("This run did not write that file.");

        var outcome = Restore(relativePath, first, last);
        if (!outcome.Undone)
            return outcome;

        lock (_journalGate)
            _journal.RemoveAll(w => string.Equals(w.RelativePath, relativePath, WorkspaceGuard.Comparison));

        return outcome;
    }

    /// <summary>
    /// Puts the named paths back to how they were when <paramref name="owner"/> was created — see
    /// the contract on <see cref="IArtifactScope.RevertAsync"/> for why this exists.
    /// </summary>
    public Task<RevertReport> RevertOwnedAsync(
        int owner, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var reverted = new List<string>();
        var kept = new List<string>();

        int position;
        lock (_journalGate)
            position = owner >= 0 && owner < _ownerPositions.Count
                ? _ownerPositions[owner]
                : 0;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The state to go back to is the one the FIRST write after the checkpoint displaced; the
            // file on disk has to still hold what the LAST one left there.
            FileWriteRecord[] after;
            lock (_journalGate)
                after = _journal
                    .Skip(position)
                    .Where(w => string.Equals(w.RelativePath, path, WorkspaceGuard.Comparison))
                    .ToArray();

            if (after.Length == 0)
                continue;

            // Somebody else wrote this file AFTER we did. Their step may already have been accepted,
            // so putting the file back would destroy approved work — the file is no longer ours to
            // speak for. The hash check below cannot catch this: what is on disk matches their write
            // exactly, so it looks untouched. Report it instead.
            if (after[^1].Owner != owner)
            {
                kept.Add(path);
                continue;
            }

            // Only OUR entries, and the state to go back to is the one OUR FIRST write displaced —
            // which may well be a sibling step's accepted content rather than the original file.
            // Taking the oldest entry after the checkpoint instead would restore the state before
            // THEIR work too, undoing a step that was never rejected.
            var mine = after.Where(w => w.Owner == owner).ToArray();
            if (mine.Length == 0)
                continue;

            var outcome = Restore(path, mine[0], mine[^1]);
            if (!outcome.Undone)
            {
                kept.Add(path);
                continue;
            }

            reverted.Add(path);

            // Drop only the entries this scope made for this path; everything from before, and every
            // other path's writes, stay exactly where they were.
            lock (_journalGate)
                _journal.RemoveAll(w =>
                    w.Owner == owner
                    && string.Equals(w.RelativePath, path, WorkspaceGuard.Comparison));
        }

        return Task.FromResult(new RevertReport(reverted, kept));
    }

    /// <summary>
    /// Restores one path to the state <paramref name="target"/> displaced, provided the file still
    /// holds what <paramref name="current"/> left there.
    /// </summary>
    private UndoResult Restore(string relativePath, FileWriteRecord target, FileWriteRecord current)
    {
        string fullPath;
        try { fullPath = ResolveInsideRoot(relativePath); }
        catch (Exception ex) { return UndoResult.Blocked(ex.Message); }

        var currentHash = FileHash.OfFile(fullPath);

        // Both null is the removal case: the last entry deleted this path and it is still gone, so
        // the file is exactly as this store left it and the backup can go back. Comparing the two
        // hashes directly covers that as well as the ordinary "unchanged since we wrote it".
        if (currentHash is null && current.AfterHash is not null)
            return UndoResult.Blocked("The file is already gone.");

        if (!string.Equals(currentHash, current.AfterHash, StringComparison.Ordinal))
            return UndoResult.Blocked(
                "The file has changed since the run wrote it — undoing now would discard that edit.");

        try
        {
            if (!target.ExistedBefore)
            {
                File.Delete(fullPath);
                return UndoResult.Deleted;
            }

            if (target.BackupPath is null || !File.Exists(target.BackupPath))
                return UndoResult.Blocked("The previous version is no longer available.");

            File.Copy(target.BackupPath, fullPath, overwrite: true);
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
