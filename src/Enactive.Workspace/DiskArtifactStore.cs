namespace Enactive.Workspace;

using System.Collections.Concurrent;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;

/// <summary>
/// What one write did to one path, kept so it can be undone. Recorded at write time because
/// afterwards none of it is knowable: the file exists either way, and its previous bytes are gone.
/// </summary>
public sealed record FileWriteRecord(
    // As the caller spelled it, for anything a person reads.
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
    int Owner = -1,
    // WHEN, on a counter that only ever goes up. A scope's checkpoint is a value of this counter,
    // so removing entries cannot move it. It used to be the journal's LENGTH when the scope opened,
    // and "everything after the checkpoint" was Skip(that many) - which is a different set of
    // entries the moment an earlier revert removes any. Two steps rejected in one run was enough:
    // the first revert shortened the list, the second one skipped past its own write and silently
    // did nothing. Positions in a mutable list are not identity.
    long Sequence = 0,
    // The one name this FILE has - WorkspaceGuard.KeyFor, computed from the resolved full path.
    // Every lookup matches on this. RelativePath is what the caller typed, and one file arrives
    // spelled several ways in one run ("doc.txt", "./doc.txt", "a/b.txt", "a\b.txt"): keyed by the
    // string, one file became two records, and a revert asked about one spelling could not see the
    // other step's write under the other. Empty only for a record made before this field existed.
    string Key = "",
    long? AppendBeforeLength = null);

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
    /// Stamps every journal entry, under <see cref="_journalGate"/>. Monotonic and never reused, so
    /// a checkpoint taken from it stays meaningful however much of the journal is later removed.
    /// </summary>
    private long _sequence;

    /// <summary>
    /// One lock per FILE, held across the whole of a write: back up what is there, replace it, and
    /// journal what happened. Two parallel steps writing one path used to race, and the race had two
    /// outcomes, both bad.
    ///
    /// <para>The visible one: <see cref="AtomicWrite"/> builds the new content beside the target and
    /// moves it into place, and on Windows two moves onto one path collide - the loser got "Access
    /// to the path is denied", a Win32 message no model can act on, and its step was marked
    /// Incomplete for a collision the engine itself had caused.</para>
    ///
    /// <para>The one that would have been worse: the sequence number was taken AFTER the write,
    /// under a different lock than the write. Two writers could therefore land on disk in one order
    /// and be journalled in the other - and every revert decision, including whose write came after
    /// whose, is read off that order. A journal that disagrees with the disk is not a journal.</para>
    ///
    /// <para>Keyed by <see cref="WorkspaceGuard.KeyFor"/>, so the two spellings of one path take the
    /// same lock - the same reason the journal is keyed that way.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim GateFor(string key)
        => _fileGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

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
        AppendRecoveryConflicts = AppendIntent.Recover(_root);
    }

    /// <summary>Interrupted appends left untouched because their content no longer matches the intent.</summary>
    public IReadOnlyList<string> AppendRecoveryConflicts { get; }
    public string Root => _root;

    /// <summary>
    /// The journal key for a path as the caller spelled it. A path that cannot be resolved has no
    /// key and matches nothing - which is the safe answer: an unresolvable path is not a file this
    /// store ever wrote.
    /// </summary>
    private string? KeyOrNull(string relativePath)
    {
        try { return WorkspaceGuard.KeyFor(_root, ResolveInsideRoot(relativePath)); }
        catch { return null; }
    }

    /// <summary>Do these two journal keys name the same file?</summary>
    private static bool SameFile(string a, string b) => string.Equals(a, b, WorkspaceGuard.Comparison);

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
    /// <summary>
    /// Whether the version the LAST write displaced was kept - which is the contract, "the version
    /// this store displaced". It read the FIRST write of the run until 2026-09-24, which answers a
    /// different question (was the file there before the run at all), and so a file the run itself
    /// created was reported as having lost its previous version on every later write: run
    /// a2142be6, "REPLACED the existing file 'Docs/DRIFT_ollama.md' ... Its previous version could
    /// NOT be backed up and is gone" - while that very write had backed up step 1's version, and a
    /// revert of step 2 would have put it back. The reviewer is handed that sentence as ground
    /// truth.
    /// </summary>
    public bool CanRestore(string relativePath)
        => LastWrite(relativePath) is { ExistedBefore: true } write
           && (write.AppendBeforeLength is not null || write.BackupPath is { } backup && File.Exists(backup));

    /// <summary>The state this path was in before the run first touched it.</summary>
    public FileWriteRecord? FirstWrite(string relativePath)
    {
        if (KeyOrNull(relativePath) is not { } key)
            return null;

        lock (_journalGate)
            return _journal.FirstOrDefault(w => SameFile(w.Key, key));
    }

    /// <summary>What the last write to this path left behind.</summary>
    public FileWriteRecord? LastWrite(string relativePath)
    {
        if (KeyOrNull(relativePath) is not { } key)
            return null;

        lock (_journalGate)
            return _journal.LastOrDefault(w => SameFile(w.Key, key));
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
            _ownerCheckpoints.Add(_sequence);
            return _ownerCheckpoints.Count - 1;
        }
    }

    public IArtifactScope BeginStep() => new ArtifactScope(this, NewOwner());

    /// <summary>
    /// Every path this owner wrote or removed, from the journal — the record made when the operation
    /// happened, not a reading of what the model said it would do.
    /// </summary>
    /// <summary>
    /// Of the paths this owner wrote, the ones ANOTHER owner also wrote at or after the moment
    /// this scope opened — that is, while it was live.
    ///
    /// <para>Everything needed was already recorded, for the revert. Owner says who, Sequence says
    /// when on a counter that only goes up, and Key is the one name a file has however it was
    /// spelled. The question had simply never been asked.</para>
    /// </summary>
    public IReadOnlyCollection<string> AlsoWrittenByAnother(int owner)
    {
        lock (_journalGate)
        {
            if (owner < 0 || owner >= _ownerCheckpoints.Count)
                return Array.Empty<string>();

            var opened = _ownerCheckpoints[owner];

            var mine = _journal
                .Where(w => w.Owner == owner)
                .Select(w => w.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (mine.Count == 0)
                return Array.Empty<string>();

            return _journal
                .Where(w => w.Owner != owner && w.Sequence >= opened && mine.Contains(w.Key))
                .Select(w => w.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public IReadOnlyCollection<string> TouchedBy(int owner)
    {
        // Keys, not spellings: a scope that wrote "doc.txt" and then "./doc.txt" touched ONE file,
        // and handing the caller both would have it revert the same path twice.
        lock (_journalGate)
            return _journal
                .Where(w => w.Owner == owner)
                .Select(w => w.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    /// <summary>
    /// The value of <see cref="_sequence"/> each owner was created at, indexed by owner id. Not a
    /// position in <see cref="_journal"/>: that moves.
    /// </summary>
    private readonly List<long> _ownerCheckpoints = new();

    /// <summary>A write made outside any step's scope. It is nobody's, and nobody can undo it.</summary>
    public Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
        => CreateAsync(relativePath, kind, title, write, Unowned, ct);

    public bool CanCheckVersion => true;
    public Task<ArtifactRef> CreateCheckedAsync(string path, ArtifactKind kind, string title,
        Func<Stream, Task> write, ArtifactVersion expected, CancellationToken ct)
        => CreateCoreAsync(path, kind, title, write, Unowned, ct, expected);
    public Task<ArtifactRef> CreateCheckedAsync(string path, ArtifactKind kind, string title,
        Func<Stream, Task> write, ArtifactVersion expected, int owner, CancellationToken ct)
        => CreateCoreAsync(path, kind, title, write, owner, ct, expected);
    public Task<ArtifactRef> CreateAsync(string relativePath, ArtifactKind kind, string title,
        Func<Stream, Task> write, int owner, CancellationToken ct)
        => CreateCoreAsync(relativePath, kind, title, write, owner, ct);

    private async Task<ArtifactRef> CreateCoreAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write,
        int owner, CancellationToken ct, ArtifactVersion? expected = null)
    {
        var fullPath = ResolveInsideRoot(relativePath);
        var key = WorkspaceGuard.KeyFor(_root, fullPath);

        // The worker's own working area is not the work. It is written, it is readable, and it is
        // deliberately absent from everything downstream: no backup, no journal entry, and so no
        // appearance in TouchedBy - which is what the reviewer is shown as "what this step
        // changed" and what a rejected step's revert is asked to put back. A helper script the
        // agent wrote to do the job is not a change to the project, and judging it as one is how a
        // sound piece of work gets rejected over its scaffolding.
        var scratch = WorkspaceGuard.IsScratch(_root, fullPath);
        if (scratch) SweepScratchOnce();

        // Everything below is one operation as far as this file is concerned - see _fileGates.
        var gate = GateFor(key);
        await gate.WaitAsync(ct);
        try
        {
            if (expected is not null && FileHash.OfFile(fullPath) != expected.Hash)
                throw new IOException("File changed since it was read; read the current version and retry the edit.");
            var existed = !scratch && File.Exists(fullPath);
            var beforeHash = existed ? FileHash.OfFile(fullPath) : null;
            var backupPath = existed ? BackUp(fullPath) : null;
            PreserveAppendPrefixes(key, backupPath);

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Built somewhere else and moved into place. Opening the target itself with
            // FileMode.Create emptied it before the first byte arrived, so a provider that threw
            // halfway — or a cancellation, or a full disk — replaced the user's file with a
            // fragment, and nothing was journalled because the entry was only added on success. An
            // exception now propagates with the file exactly as it was.
            await AtomicWrite.Replace(fullPath, write);

            if (!scratch)
            {
                var afterHash = FileHash.OfFile(fullPath)!;
                lock (_journalGate)
                    _journal.Add(new FileWriteRecord(
                        relativePath, existed, beforeHash, backupPath, afterHash, owner, ++_sequence, key));
            }
        }
        finally
        {
            gate.Release();
        }

        var id = Guid.NewGuid();
        _paths[id] = fullPath;
        return new ArtifactRef(id, kind, title, relativePath);
    }

    /// <summary>
    /// Deletes a file and journals the deletion, so a rejected step or an Undo puts it back. The
    /// backup is taken first; the journal entry is published only after a successful deletion,
    /// under the same file gate as writes and rollback.
    /// </summary>
    /// <summary>This store deletes files, and journals the deletion so it can be undone.</summary>
    public bool CanRemove => true;

    public Task RemoveAsync(string relativePath, CancellationToken ct)
        => RemoveAsync(relativePath, Unowned, ct);

    /// <inheritdoc cref="RemoveAsync(string, CancellationToken)"/>
    public async Task RemoveAsync(string relativePath, int owner, CancellationToken ct)
    {
        var fullPath = ResolveInsideRoot(relativePath);
        var key = WorkspaceGuard.KeyFor(_root, fullPath);

        var gate = GateFor(key);
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException(
                    $"No such file in this workspace: {relativePath}", relativePath);

            // The same rule as the write, and for the same reason: the worker's own working area is
            // not the work. Journalling a scratch deletion would put it in front of the reviewer as
            // something the step removed, and make a rejected step restore a file nobody wanted
            // back. Tidying up after itself must not read as a change to the project.
            if (WorkspaceGuard.IsScratch(_root, fullPath))
            {
                File.Delete(fullPath);
                return;
            }

            var beforeHash = FileHash.OfFile(fullPath);
            var backupPath = BackUp(fullPath);
            PreserveAppendPrefixes(key, backupPath);

            if (backupPath is null)
                throw new IOException(
                    $"Could not keep a copy of '{relativePath}', so removing it could not be undone. "
                    + "Nothing was deleted.");

            File.Delete(fullPath);
            lock (_journalGate)
                _journal.Add(new FileWriteRecord(
                    relativePath, ExistedBefore: true, beforeHash, backupPath, AfterHash: null, owner,
                    ++_sequence, key));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Undoes this store's whole effect on a path: a file that existed before the run is restored,
    /// one the run created is deleted. Only when the file still holds exactly what the run left
    /// there — otherwise undoing would throw away an edit made since.
    /// </summary>
    public UndoResult Undo(string relativePath)
    {
        if (KeyOrNull(relativePath) is not { } lockedKey) return UndoResult.Blocked("Invalid path.");
        var gate = GateFor(lockedKey);
        if (!gate.Wait(0)) return UndoResult.Blocked("A write is in progress; retry undo after it finishes.");
        try
        {
            var first = FirstWrite(relativePath);
            var last = LastWrite(relativePath);
            if (first is null || last is null)
                return UndoResult.Blocked("This run did not write that file.");

            var outcome = Restore(relativePath, first, last);
            if (!outcome.Undone)
                return outcome;

            if (KeyOrNull(relativePath) is { } key)
                lock (_journalGate)
                    _journal.RemoveAll(w => SameFile(w.Key, key));

            return outcome;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Puts the named paths back to how they were when <paramref name="owner"/> was created — see
    /// the contract on <see cref="IArtifactScope.RevertAsync"/> for why this exists.
    /// </summary>
    public async Task<RevertReport> RevertOwnedAsync(
        int owner, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var reverted = new List<string>();
        var kept = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Keep(string path, string reason)
        {
            kept.Add(path);
            reasons[path] = reason;
        }

        // A value of the write counter, not a position in the journal. See FileWriteRecord.Sequence:
        // reverting one scope removes its entries, and every position after them meant something
        // else afterwards.
        long checkpoint;
        lock (_journalGate)
            checkpoint = owner >= 0 && owner < _ownerCheckpoints.Count
                ? _ownerCheckpoints[owner]
                : 0;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // One file, whatever spelling arrived. TouchedPaths already hands back keys, but a
            // caller is free to pass its own list and a model's two spellings must not become two
            // paths here either.
            if (KeyOrNull(path) is not { } key)
            {
                Keep(path, "that path cannot be resolved inside this workspace");
                continue;
            }

            var gate = GateFor(key);
            await gate.WaitAsync(ct);
            try
            {
                // The state to go back to is the one the FIRST write after the checkpoint displaced; the
                // file on disk has to still hold what the LAST one left there.
                FileWriteRecord[] after;
                lock (_journalGate)
                    after = _journal
                        .Where(w => w.Sequence > checkpoint && SameFile(w.Key, key))
                        .ToArray();

                // Nothing on record for a path we were ASKED about. Said out loud rather than skipped:
                // the caller cannot see the journal, and a silent pass reads as "reverted". Reaching
                // here means the journal disagrees with whoever supplied the path list.
                if (after.Length == 0)
                {
                    Keep(path, "nothing this step wrote to it is on record");
                    continue;
                }

                // Only OUR entries, and the state to go back to is the one OUR FIRST write displaced -
                // which may well be a sibling step's accepted content rather than the original file.
                // Taking the oldest entry after the checkpoint instead would restore the state before
                // THEIR work too, undoing a step that was never rejected.
                var mine = after.Where(w => w.Owner == owner).ToArray();
                if (mine.Length == 0)
                {
                    Keep(path, "this step made no write to it that is still on record");
                    continue;
                }

                // Somebody else wrote this file after we first touched it. Their step may already have
                // been accepted, so putting the file back would destroy approved work - it is no longer
                // ours to speak for. The hash check below cannot catch this: what is on disk matches
                // whoever wrote last, so it looks untouched.
                //
                // The question is whether ANYONE else wrote it after our first write, not whether the
                // LAST write is ours. A → B → A passed the old check, because the last entry was ours -
                // and then restoring "the state our first write displaced" threw away B's accepted
                // content sitting in the middle. A foreign write BEFORE our first one is the documented
                // case above and is fine: our first write displaced their content, and that is exactly
                // what goes back.
                var foreign = after.FirstOrDefault(
                    w => w.Owner != owner && w.Sequence > mine[0].Sequence);
                if (foreign is not null)
                {
                    Keep(path, "another step wrote it after this one did");
                    continue;
                }

                var outcome = Restore(path, mine[0], mine[^1]);
                if (!outcome.Undone)
                {
                    Keep(path, outcome.Conflict ?? "it could not be put back");
                    continue;
                }

                reverted.Add(path);

                // Drop only the entries this scope made for this path; everything from before, and every
                // other path's writes, stay exactly where they were. Other scopes' checkpoints are
                // counter values and are unaffected by this.
                lock (_journalGate)
                    _journal.RemoveAll(w => w.Owner == owner && SameFile(w.Key, key));
            }
            finally { gate.Release(); }
        }

        return new RevertReport(reverted, kept, reasons);
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

            if (target.AppendBeforeLength is { } length)
            {
                var source = target.BackupPath ?? fullPath;
                using var prefix = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (prefix.Length < length || HashPrefix(prefix, length) != target.BeforeHash)
                    return UndoResult.Blocked("The original prefix changed; append cannot be undone safely.");
                if (target.BackupPath is null)
                {
                    using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    file.SetLength(length);
                    file.Flush(flushToDisk: true);
                }
                else
                {
                    prefix.Position = 0;
                    AtomicWrite.ReplaceFrom(fullPath, prefix, length);
                }
                return UndoResult.RestoredPrevious;
            }

            if (target.BackupPath is null || !File.Exists(target.BackupPath))
                return UndoResult.Blocked("The previous version is no longer available.");

            using var backup = new FileStream(target.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            AtomicWrite.ReplaceFrom(fullPath, backup);
            return UndoResult.RestoredPrevious;
        }
        catch (Exception ex)
        {
            return UndoResult.Blocked(ex.Message);
        }
    }

    public bool CanAppend => true;

    public Task<ArtifactRef> AppendAsync(string relativePath, ArtifactKind kind, string title,
        Func<Stream?, Stream, Task> writeTail, CancellationToken ct)
        => AppendAsync(relativePath, kind, title, writeTail, Unowned, ct);

    public async Task<ArtifactRef> AppendAsync(string relativePath, ArtifactKind kind, string title,
        Func<Stream?, Stream, Task> writeTail, int owner, CancellationToken ct)
    {
        var full = ResolveInsideRoot(relativePath);
        var key = WorkspaceGuard.KeyFor(_root, full);
        if (!File.Exists(full))
            return await CreateAsync(relativePath, kind, title, async output =>
            {
                // New files retain atomic replacement. Another writer may create it before
                // CreateAsync takes the gate, in which case append to that version instead.
                await using var input = File.Exists(full)
                    ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
                if (input is not null) { await input.CopyToAsync(output, ct); input.Position = 0; }
                await writeTail(input, output);
            }, owner, ct);
        var gate = GateFor(key);
        await gate.WaitAsync(ct);
        try
        {
            var scratch = WorkspaceGuard.IsScratch(_root, full);
            if (scratch) SweepScratchOnce();
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var existed = File.Exists(full);
            if (!existed)
            {
                await AtomicWrite.Replace(full, output => writeTail(null, output));
                if (!scratch)
                    lock (_journalGate)
                        _journal.Add(new FileWriteRecord(relativePath, false, null, null,
                            FileHash.OfFile(full), owner, ++_sequence, key));
                var created = Guid.NewGuid();
                _paths[created] = full;
                return new ArtifactRef(created, kind, title, relativePath);
            }
            var length = 0L;
            try
            {
                // Exclude other writers/deletion for validation, preparation and commit.
                await using var file = new FileStream(full, existed ? FileMode.Open : FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.Read, 8192, FileOptions.Asynchronous);
                length = file.Length;
                var beforeHash = existed ? HashPrefix(file, length, ct) : null;
                await using var input = existed
                    ? new FileStream(full, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 8192, FileOptions.Asynchronous) : null;
                using var tail = await StagedContent.CaptureAsync(output => writeTail(input, output), ct);
                using var intentTail = tail.Open();
                using var intent = await AppendIntent.PrepareAsync(_backupRoot, relativePath, length,
                    beforeHash!, intentTail, ct);
                ct.ThrowIfCancellationRequested();
                try
                {
                    file.Position = length;
                    await using (var added = tail.Open())
                        await added.CopyToAsync(file, ct);
                    await file.FlushAsync(ct);
                    file.Position = 0;
                    var afterHash = Convert.ToHexString(
                        await System.Security.Cryptography.SHA256.HashDataAsync(file, ct));
                    file.Flush(flushToDisk: true);
                    intent.Complete();
                    if (!scratch)
                        lock (_journalGate)
                            _journal.Add(new FileWriteRecord(relativePath, existed, beforeHash,
                                null, afterHash, owner, ++_sequence, key, length));
                }
                catch
                {
                    file.SetLength(length);
                    file.Flush(flushToDisk: true);
                    intent.Complete();
                    throw;
                }
            }
            catch
            {
                if (!existed) File.Delete(full);
                throw;
            }
            var id = Guid.NewGuid();
            _paths[id] = full;
            return new ArtifactRef(id, kind, title, relativePath);
        }
        finally { gate.Release(); }
    }

    internal static string HashPrefix(Stream input, long length, CancellationToken ct = default)
    {
        input.Position = 0;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (length > 0)
        {
            ct.ThrowIfCancellationRequested();
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (count == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, count);
            length -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // A later destructive operation must retain the prefixes referenced by append records.
    private void PreserveAppendPrefixes(string key, string? backup)
    {
        lock (_journalGate)
            for (var i = 0; i < _journal.Count; i++)
            {
                var entry = _journal[i];
                if (!SameFile(entry.Key, key) || !entry.ExistedBefore || entry.AppendBeforeLength is null || entry.BackupPath is not null)
                    continue;
                if (backup is null) throw new IOException("Cannot preserve append history before replacing this file.");
                _journal[i] = entry with { BackupPath = backup };
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
            var backup = Path.Combine(_backupRoot, $"{sequence:D4}-{Guid.NewGuid():N}{Path.GetExtension(fullPath)}.bak");
            File.Copy(fullPath, backup, overwrite: false);
            return backup;
        }
        catch
        {
            return null;
        }
    }

    private int _scratchSwept;

    /// <summary>
    /// Clears what earlier runs left in the scratch area, once per store, on the way to the first
    /// write that goes there.
    ///
    /// <para>Lazy and here rather than at the three places a host starts a run: it is the same
    /// argument as <see cref="PruneOldBackups"/> - the folder's housekeeping belongs with the code
    /// that writes the folder, and a run that never uses the area should not pay for tidying it.
    /// <see cref="ScratchArea.Sweep"/> swallows its own failures, so this cannot be the reason a
    /// write fails.</para>
    /// </summary>
    private void SweepScratchOnce()
    {
        if (Interlocked.Exchange(ref _scratchSwept, 1) == 0)
            ScratchArea.Sweep(_root, DateTimeOffset.UtcNow);
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
                try
                {
                    // Interrupted/conflicted appends retain their recovery bytes until resolved.
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0
                        || directory.EnumerateFiles("*.append.json").Any()) continue;
                    directory.Delete(recursive: true);
                }
                catch { /* in use, or gone already */ }
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
