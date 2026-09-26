namespace Enactive.Workspace;

using System.Security.Cryptography;
using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;

/// <summary>A proposed file change held for review (not yet written to disk).</summary>
public sealed class StagedChange
{
    public StagedChange(
        Guid id, string relativePath, string key, string? oldContent, string newContent,
        int sequence, int scope = -1, string? baseHash = null)
        : this(id, relativePath, key,
            oldContent is null ? null : StagedContent.FromText(oldContent),
            StagedContent.FromText(newContent), sequence, scope, baseHash)
    {
    }

    internal StagedChange(
        Guid id, string relativePath, string key, StagedContent? oldBytes, StagedContent newBytes,
        int sequence, int scope, string? baseHash, Guid? parentId = null)
    {
        Id = id;
        RelativePath = relativePath;
        Key = key;
        _oldBytes = oldBytes;
        NewBytes = newBytes;
        Sequence = sequence;
        Scope = scope;
        BaseHash = baseHash;
        ParentId = parentId;
    }

    /// <summary>
    /// WHO proposed this: the owner of the scope it came through. A rejected step drops the
    /// proposals it made and nobody else's — with several steps sharing one store, taking the value
    /// off a shared "newest scope" field gave both steps the same number, and rejecting the second
    /// swept away the first's accepted work.
    /// </summary>
    public int Scope { get; }

    public Guid Id { get; }
    public Guid? ParentId { get; }

    /// <summary>The path as the caller spelled it. For display only — never for matching.</summary>
    public string RelativePath { get; }

    /// <summary>
    /// The one identity of the file this proposal is about: the path resolved against the workspace
    /// root and expressed relative to it again, so <c>./doc.txt</c>, <c>doc.txt</c> and
    /// <c>sub/../doc.txt</c> are one entry rather than three. Matching on the raw string meant a
    /// worker could write <c>./doc.txt</c> and then read <c>doc.txt</c> back as "File not found",
    /// and two proposals for one file were not ordered against each other.
    /// </summary>
    public string Key { get; }
    private readonly StagedContent? _oldBytes;
    internal StagedContent NewBytes { get; }
    internal StagedContent? OldBytes => _oldBytes;
    // Full text remains an explicit compatibility view, not retained storage.
    public string? OldContent => _oldBytes?.ReadText();
    public string NewContent => NewBytes.ReadText();
    public long OldByteCount => _oldBytes?.Length ?? 0;
    public long NewByteCount => NewBytes.Length;
    public bool IsBinary => NewBytes.IsBinary() || (_oldBytes?.IsBinary() ?? false);

    /// <summary>
    /// Position in the run's write order. Two proposals for the same path must be applied oldest
    /// first: applying them out of order silently reinstates an earlier version.
    /// </summary>
    public int Sequence { get; }

    /// <summary>
    /// Hash of the exact bytes expected on disk when applied: the preceding proposal's bytes,
    /// or the original file's bytes. Null means the file must not exist.
    /// </summary>
    public string? BaseHash { get; }

    public bool IsNew => _oldBytes is null;
    public bool Applied { get; private set; }
    public bool Rejected { get; private set; }
    public bool Pending => !Applied && !Rejected;

    internal void MarkApplied() => Applied = true;
    internal void MarkRejected() => Rejected = true;
}

/// <summary>What happened when a staged change was applied.</summary>
public sealed record ApplyResult(bool Applied, string? Conflict = null)
{
    public static readonly ApplyResult Ok = new(true);
    public static ApplyResult Blocked(string reason) => new(false, reason);
}

public sealed record RejectResult(bool Rejected, string? Conflict = null);

/// <summary>Content hashing for the concurrency checks. SHA-256, hex.</summary>
public static class FileHash
{
    /// <summary>Of a string, as UTF-8. For content that IS text and never was a file.</summary>
    public static string? Of(string? content)
        => content is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Of the file's BYTES. It used to hash <c>File.ReadAllText</c>, which is not a hash of the file:
    /// every byte the UTF-8 decoder cannot represent became the same replacement character before
    /// the hash was taken, so 0xFF and 0xFE hashed identically. Undo compares this against what it
    /// wrote to decide whether a file has been edited since — and judged a user's edit to a binary
    /// file to be its own work, and deleted it.
    ///
    /// <para>Not a SHA-256 collision: the information was gone before the hash function saw it.</para>
    /// </summary>
    public static string? OfFile(string fullPath)
    {
        if (!File.Exists(fullPath)) return null;
        using var stream = File.OpenRead(fullPath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Of bytes already in hand — so a caller that must read a file anyway reads it once.</summary>
    public static string OfBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>
/// An <see cref="IArtifactStore"/> that stages writes instead of committing them: the proposed content
/// is captured (with the previous content for a diff) and only written to disk on <see cref="Apply"/>.
/// Lets the UI show a diff with Apply/Reject before anything touches the workspace.
///
/// Two properties this did not have:
///
/// 1. <b>Staged writes are readable.</b> Proposals lived only in this object, so the write-then-read
///    verification the workers are instructed to do returned the old content or "File not found",
///    and a dependent step could not see a file the previous step had just written — while every
///    message said the result was on disk.
/// 2. <b>Apply does not destroy other edits.</b> The old content was captured for the diff and then
///    never compared to anything: if the user (or another task) changed the file after the proposal
///    was made, <c>File.WriteAllText</c> quietly erased that change.
///
/// Still true, and stated rather than papered over: proposals use private temporary files and do not survive
/// closing the app, and shell tools work on the real folder, so a staged run is not a transaction.
/// </summary>
public sealed class StagingArtifactStore : IOwnedArtifactStore, IDisposable
{
    private readonly string _root;
    private readonly List<StagedChange> _changes = new();
    private readonly object _gate = new();
    private int _sequence;
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>Owner ids handed out so far. -1 is "nobody", and nobody's proposals are undone.</summary>
    private int _owners = -1;
    private const int Unowned = -1;

    public StagingArtifactStore(string workspaceRoot) => _root = Path.GetFullPath(workspaceRoot);

    public IReadOnlyList<StagedChange> Changes
    {
        get { lock (_gate) return _changes.ToArray(); }
    }

    public IReadOnlyCollection<string> PendingPaths
    {
        get
        {
            lock (_gate)
                return _changes.Where(c => c.Pending)
                               .Select(c => c.Key)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToArray();
        }
    }

    /// <summary>A proposal made outside any step's scope. It is nobody's, and nobody can drop it.</summary>
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
        var full = ResolveInside(relativePath);
        var key = KeyOf(full);

        // The worker's own working area is written STRAIGHT THROUGH, and here that is not a
        // refinement but the thing that makes it usable at all. Staging holds a proposal and puts
        // nothing on disk until somebody presses Apply; a helper script staged that way does not
        // exist for the `run_command` that was written to run it, and the step fails on a file it
        // has just been told it created. Nor is there anything for a person to approve: a diff of
        // a throwaway is a question with no useful answer.
        await _writes.WaitAsync(ct);
        StagedContent? captured = null, original = null;
        try
        {
            if (expected is not null && (NewestPending(key)?.NewBytes.Hash ?? FileHash.OfFile(full)) != expected.Hash)
                throw new IOException("File changed since it was read; read the current version and retry the edit.");
            if (WorkspaceGuard.IsScratch(_root, full))
            {
                SweepScratchOnce();

                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                await AtomicWrite.Replace(full, write);

                return new ArtifactRef(Guid.NewGuid(), kind, title, relativePath);
            }

            var pending = NewestPending(key);
            if (pending is null && File.Exists(full))
                original = await StagedContent.CaptureAsync(async output =>
                {
                    await using var input = new FileStream(full, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
                    await input.CopyToAsync(output, ct);
                }, ct);
            captured = await StagedContent.CaptureAsync(write, ct);
            var oldContent = pending?.NewBytes ?? original;
            var id = Guid.NewGuid();
            lock (_gate)
                _changes.Add(new StagedChange(id, relativePath, key, oldContent, captured,
                    _sequence++, owner, oldContent?.Hash, pending?.Id));
            captured = original = null; // ownership passed to the change
            return new ArtifactRef(id, kind, title, relativePath);
        }
        finally
        {
            captured?.Dispose();
            original?.Dispose();
            _writes.Release();
        }
    }

    public Task<Stream> OpenAsync(Guid artifactId, CancellationToken ct)
    {
        StagedChange? change;
        lock (_gate)
            change = _changes.Find(c => c.Id == artifactId);

        if (change is null)
            throw new FileNotFoundException("Unknown staged artifact.", artifactId.ToString());

        Stream stream = change.NewBytes.Open();
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid artifactId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Reject(artifactId);
        if (!result.Rejected) throw new IOException(result.Conflict);
        return Task.CompletedTask;
    }

    /// <summary>The newest pending proposal for a path, or null when there is none.</summary>
    public Task<string?> TryReadPendingAsync(string relativePath, CancellationToken ct)
    {
        string key;
        try { key = KeyOf(ResolveInside(relativePath)); }
        catch (ArgumentException) { return Task.FromResult<string?>(null); }

        return Task.FromResult(NewestPending(key)?.NewContent);
    }

    private StagedChange? NewestPending(string key)
    {
        lock (_gate)
        {
            for (var i = _changes.Count - 1; i >= 0; i--)
            {
                var change = _changes[i];
                if (change.Pending && string.Equals(change.Key, key, WorkspaceGuard.Comparison))
                    return change;
            }
        }

        return null;
    }

    public Task<Stream?> TryOpenPendingAsync(string relativePath, CancellationToken ct)
    {
        var change = NewestPending(KeyOf(ResolveInside(relativePath)));
        return Task.FromResult<Stream?>(change is null ? null : change.NewBytes.Open());
    }

    /// <summary>
    /// Commit a staged change to disk, unless the workspace moved underneath it. A conflict is
    /// REPORTED, never resolved by overwriting: the whole point of staging is that the user decides.
    /// </summary>
    public ApplyResult Apply(Guid id)
    {
        if (!_writes.Wait(0)) return ApplyResult.Blocked("A proposal is being written; retry after it finishes.");
        try { return ApplyCore(id); }
        finally { _writes.Release(); }
    }

    private ApplyResult ApplyCore(Guid id)
    {
        StagedChange? change;
        lock (_gate)
            change = _changes.Find(c => c.Id == id);

        if (change is null)
            return ApplyResult.Blocked("This change is no longer known.");
        if (change.Applied)
            return ApplyResult.Blocked("Already applied.");
        if (change.Rejected)
            return ApplyResult.Blocked("Already rejected.");

        // Older proposals for the same path go first, or applying this one reinstates their "before".
        lock (_gate)
        {
            var earlier = _changes.FirstOrDefault(c =>
                c.Pending
                && c.Sequence < change.Sequence
                && string.Equals(c.Key, change.Key, WorkspaceGuard.Comparison));

            if (earlier is not null)
                return ApplyResult.Blocked(
                    "An earlier change to this file is still pending — apply or reject that one first.");
        }

        var full = ResolveInside(change.RelativePath);

        var currentHash = FileHash.OfFile(full);
        if (!string.Equals(currentHash, change.BaseHash, StringComparison.Ordinal))
        {
            return ApplyResult.Blocked(
                currentHash is null
                    ? "The file has been deleted since this change was proposed."
                    : change.BaseHash is null
                        ? "A file now exists at this path; this change was proposed as a new file."
                        : "The file changed on disk after this change was proposed — applying it would "
                          + "discard that edit. Review the file, then reject this change or redo the work.");
        }

        try
        {
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write beside the target and move into place: an interrupted apply must not leave the
            // user with half a file.
            using var input = change.NewBytes.Open();
            AtomicWrite.ReplaceFrom(full, input);
        }
        catch (Exception ex)
        {
            return ApplyResult.Blocked("Could not write the file: " + ex.Message);
        }

        change.MarkApplied();
        return ApplyResult.Ok;
    }

    /// <summary>Discard only a leaf proposal; descendants must be rejected first.</summary>
    public RejectResult Reject(Guid id)
    {
        if (!_writes.Wait(0)) return new(false, "A proposal is being written; retry after it finishes.");
        try
        {
            lock (_gate)
            {
                var change = _changes.Find(c => c.Id == id);
                if (change is null || !change.Pending) return new(false, "This change is no longer pending.");
                if (_changes.Any(c => !c.Rejected && DescendsFrom(c, change.Id)))
                    return new(false, "Later proposals depend on this change; reject them first.");
                change.MarkRejected();
                return new(true);
            }
        }
        finally { _writes.Release(); }
    }

    private bool DescendsFrom(StagedChange change, Guid ancestor)
    {
        var parent = change.ParentId;
        while (parent is { } id)
        {
            if (id == ancestor) return true;
            parent = _changes.Find(c => c.Id == id)?.ParentId;
        }
        return false;
    }

    public IReadOnlyCollection<string> PendingBy(int owner)
    {
        lock (_gate)
            return _changes.Where(c => c.Pending && c.Scope == owner).Select(c => c.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// A new owner id — the same contract as the disk store's. A proposal carries the owner of the
    /// scope that made it, so a rejected step drops ITS proposals and leaves a concurrent step's
    /// alone. The owner comes from the scope, never from a shared "newest" field: that field gave
    /// two interleaved steps the same number, and rejecting the second dropped the first's work.
    /// </summary>
    /// <summary>
    /// Where the change list stood when this scope opened. Needed only by
    /// <see cref="AlsoWrittenByAnother"/>: without it, "another step wrote this while I was open"
    /// cannot be told from "an earlier step wrote it before I started", and the second is the
    /// ordinary way a plan builds one document.
    /// </summary>
    private readonly Dictionary<int, int> _openedAt = new();

    public int NewOwner()
    {
        lock (_gate)
        {
            var owner = ++_owners;
            _openedAt[owner] = _sequence;
            return owner;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> AlsoWrittenByAnother(int owner)
    {
        lock (_gate)
        {
            if (!_openedAt.TryGetValue(owner, out var opened))
                return Array.Empty<string>();

            var mine = _changes
                .Where(c => c.Scope == owner)
                .Select(c => c.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (mine.Count == 0)
                return Array.Empty<string>();

            return _changes
                .Where(c => c.Scope != owner && c.Sequence >= opened && mine.Contains(c.Key))
                .Select(c => c.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public IArtifactScope BeginStep() => new ArtifactScope(this, NewOwner());

    /// <summary>
    /// Staging holds proposals, and a proposal cannot say "this file is gone" — see the contract on
    /// <see cref="IArtifactStore.RemoveAsync(string, CancellationToken)"/>. The caller must report
    /// that rather than delete the file itself and leave the staging record lying about the state.
    /// </summary>
    /// <summary>
    /// A removal outside any step's scope. It was never overridden here, so it fell through to the
    /// interface's throwing default - which was right while nothing in this store could be removed
    /// at all, and wrong the moment the scratch area could: the owned overload below grew a scratch
    /// case and this one did not see it, so the tool asked, was told it could go ahead, and then
    /// got the default's exception anyway.
    /// </summary>
    public Task RemoveAsync(string relativePath, CancellationToken ct)
        => RemoveAsync(relativePath, Unowned, ct);

    public Task RemoveAsync(string relativePath, int owner, CancellationToken ct)
    {
        // The worker's own working area was never staged, so there is no proposal here to be
        // unable to express: the file is on disk and removing it is just removing it. Without
        // this, an agent could create a scratch file under staging and then not be allowed to
        // clear up after itself, which is a strange shape to leave a tool in.
        var full = ResolveInside(relativePath);
        if (WorkspaceGuard.IsScratch(_root, full))
        {
            if (File.Exists(full))
                File.Delete(full);
            return Task.CompletedTask;
        }

        throw new NotSupportedException("Staged changes cannot express a deletion.");
    }

    /// <summary>
    /// No — for the workspace proper. A proposal is a file's next content; there is no way to
    /// propose its absence. Said here so an operation that needs a removal can decline BEFORE
    /// doing the half of itself that works — move_file used to write the destination proposal and
    /// only then discover this.
    ///
    /// <para>It stays false with the scratch exception above in place, and that is deliberate: this
    /// property is asked WITHOUT a path, so the only honest answer it can give is the one that
    /// holds for the workspace it is staging. A caller that knows it is looking at the scratch area
    /// knows more than this property does — see <c>DeleteFileTool</c>, which asks about the path it
    /// actually has.</para>
    /// </summary>
    public bool CanRemove => false;

    private int _scratchSwept;

    /// <summary>
    /// Clears what earlier runs left in the scratch area, once per store, before the first write
    /// that goes there. The same lazy housekeeping <c>DiskArtifactStore</c> does, because a staged
    /// run writes that folder just as directly as an unstaged one.
    /// </summary>
    private void SweepScratchOnce()
    {
        if (Interlocked.Exchange(ref _scratchSwept, 1) == 0)
            ScratchArea.Sweep(_root, DateTimeOffset.UtcNow);
    }

    /// <summary>Every path this owner proposed a change to — the canonical key, not the spelling.</summary>
    public IReadOnlyCollection<string> TouchedBy(int owner)
    {
        lock (_gate)
            return _changes
                .Where(c => c.Scope == owner)
                .Select(c => c.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    /// <summary>Reject this owner's pending versions only when no foreign version depends on them.</summary>
    public async Task<RevertReport> RevertOwnedAsync(
        int owner, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var reverted = new List<string>();
        var kept = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await _writes.WaitAsync(ct);
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string key;
                try { key = KeyOf(ResolveInside(path)); }
                catch (ArgumentException)
                {
                    kept.Add(path); reasons[path] = "Invalid workspace path."; continue;
                }
                lock (_gate)
                {
                    var mine = _changes.Where(c => c.Key.Equals(key, WorkspaceGuard.Comparison)
                        && c.Scope == owner && !c.Rejected).ToArray();
                    string? conflict = mine.Any(c => c.Applied) ? "This step has changes already applied to disk."
                        : _changes.Any(c => c.Scope != owner && !c.Rejected && mine.Any(m => DescendsFrom(c, m.Id)))
                            ? "Another step's proposal depends on this version; nothing was reverted."
                            : mine.Length == 0 ? "No pending changes owned by this step." : null;
                    if (conflict is not null)
                    {
                        kept.Add(key); reasons[key] = conflict; continue;
                    }
                    foreach (var change in mine) change.MarkRejected();
                    reverted.Add(key);
                }
            }
        }
        finally { _writes.Release(); }
        return new RevertReport(reverted, kept, reasons);
    }

    /// <summary>The shared rule — see the same note on <see cref="DiskArtifactStore"/>.</summary>
    private string ResolveInside(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is empty.", nameof(relativePath));

        return WorkspaceGuard.ResolveInside(_root, relativePath);
    }

    /// <summary>
    /// The identity of a file inside this workspace: its resolved location, said relative to the
    /// root, with one separator. Everything that has to decide "is this the same file?" — pending
    /// reads, apply ordering, revert — compares this and nothing else.
    /// </summary>
    private string KeyOf(string full)
        => Path.GetRelativePath(_root, full)
               .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    /// <summary>Release retained proposals after their consumers (including review UI) are finished.</summary>
    public void Dispose()
    {
        foreach (var content in Changes.SelectMany(c => new[] { c.OldBytes, c.NewBytes }).OfType<StagedContent>().Distinct())
            content.Dispose();
    }

}
