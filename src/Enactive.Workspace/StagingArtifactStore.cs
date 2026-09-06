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
        int sequence, int scope = -1)
    {
        Id = id;
        RelativePath = relativePath;
        Key = key;
        OldContent = oldContent;
        NewContent = newContent;
        Sequence = sequence;
        Scope = scope;
        BaseHash = FileHash.Of(oldContent);
    }

    /// <summary>
    /// Which revert scope proposed this. A rejected step drops the proposals it made and nobody
    /// else's — with several steps sharing one store, an earlier checkpoint used to sweep away a
    /// later step's still-pending work.
    /// </summary>
    public int Scope { get; }

    public Guid Id { get; }

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
    public string? OldContent { get; }
    public string NewContent { get; }

    /// <summary>
    /// Position in the run's write order. Two proposals for the same path must be applied oldest
    /// first: applying them out of order silently reinstates an earlier version.
    /// </summary>
    public int Sequence { get; }

    /// <summary>
    /// Hash of the file as it was when this proposal was made (null when the file did not exist).
    /// Apply compares it against the file on disk, so an edit made in the meantime is a conflict
    /// rather than something to overwrite silently.
    /// </summary>
    public string? BaseHash { get; }

    public bool IsNew => OldContent is null;
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

/// <summary>Content hashing for the concurrency checks. SHA-256 over UTF-8, hex.</summary>
public static class FileHash
{
    public static string? Of(string? content)
        => content is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static string? OfFile(string fullPath)
        => File.Exists(fullPath) ? Of(File.ReadAllText(fullPath)) : null;
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
/// Still true, and stated rather than papered over: proposals are held in memory and do not survive
/// closing the app, and shell tools work on the real folder, so a staged run is not a transaction.
/// </summary>
public sealed class StagingArtifactStore : IArtifactStore
{
    private readonly string _root;
    private readonly List<StagedChange> _changes = new();
    private readonly object _gate = new();
    private int _sequence;

    /// <summary>The newest revert scope handed out by <see cref="Checkpoint"/>; -1 = none yet.</summary>
    private int _scope = -1;

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

    public async Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
    {
        // Capture the proposed content without touching disk.
        await using var buffer = new MemoryStream();
        await write(buffer);
        var newContent = Encoding.UTF8.GetString(buffer.ToArray());

        var full = ResolveInside(relativePath);
        var key = KeyOf(full);

        // The base for the diff and for the conflict check is what a reader would see NOW: an earlier
        // pending proposal for the same path, else the file on disk. Diffing against the disk while a
        // proposal is already outstanding shows a change the user never made.
        var pending = NewestPending(key);
        var oldContent = pending ?? (File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : null);

        var id = Guid.NewGuid();
        lock (_gate)
            _changes.Add(new StagedChange(
                id, relativePath, key, oldContent, newContent, _sequence++, _scope));

        return new ArtifactRef(id, kind, title, relativePath);
    }

    public Task<Stream> OpenAsync(Guid artifactId, CancellationToken ct)
    {
        StagedChange? change;
        lock (_gate)
            change = _changes.Find(c => c.Id == artifactId);

        if (change is null)
            throw new FileNotFoundException("Unknown staged artifact.", artifactId.ToString());

        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(change.NewContent));
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid artifactId, CancellationToken ct)
    {
        Reject(artifactId);
        return Task.CompletedTask;
    }

    /// <summary>The newest pending proposal for a path, or null when there is none.</summary>
    public Task<string?> TryReadPendingAsync(string relativePath, CancellationToken ct)
    {
        string key;
        try { key = KeyOf(ResolveInside(relativePath)); }
        catch (ArgumentException) { return Task.FromResult<string?>(null); }

        return Task.FromResult(NewestPending(key));
    }

    private string? NewestPending(string key)
    {
        lock (_gate)
        {
            for (var i = _changes.Count - 1; i >= 0; i--)
            {
                var change = _changes[i];
                if (change.Pending && string.Equals(change.Key, key, WorkspaceGuard.Comparison))
                    return change.NewContent;
            }
        }

        return null;
    }

    /// <summary>
    /// Commit a staged change to disk, unless the workspace moved underneath it. A conflict is
    /// REPORTED, never resolved by overwriting: the whole point of staging is that the user decides.
    /// </summary>
    public ApplyResult Apply(Guid id)
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
            AtomicWrite.Replace(full, change.NewContent);
        }
        catch (Exception ex)
        {
            return ApplyResult.Blocked("Could not write the file: " + ex.Message);
        }

        change.MarkApplied();
        return ApplyResult.Ok;
    }

    /// <summary>Discard a staged change.</summary>
    public void Reject(Guid id)
    {
        lock (_gate)
            _changes.Find(c => c.Id == id)?.MarkRejected();
    }

    /// <summary>
    /// Opens a revert scope and returns its id — the same contract as the disk store's. Proposals
    /// made from here on are stamped with it, so a rejected step drops ITS proposals and leaves a
    /// concurrent step's alone.
    /// </summary>
    public int Checkpoint()
    {
        lock (_gate) return ++_scope;
    }

    /// <summary>
    /// Drops the proposals made for these paths since the checkpoint — the staged equivalent of
    /// putting the files back. Nothing was written to disk, so there is nothing to restore and
    /// nothing to conflict with: a rejected step's proposals simply stop existing.
    /// </summary>
    public Task<RevertReport> RevertToAsync(
        int checkpoint, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var reverted = new List<string>();

        // The caller's paths come out of a transcript, so they carry whatever spelling the model
        // used. Canonicalise them the same way the proposals were, or "./doc.txt" and "doc.txt"
        // describe the same file and match nothing.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try { wanted.Add(KeyOf(ResolveInside(path))); }
            catch (ArgumentException) { /* a path no proposal could have used */ }
        }

        lock (_gate)
        {
            foreach (var change in _changes)
            {
                if (change.Scope != checkpoint || !change.Pending)
                    continue;

                if (!wanted.Contains(change.Key))
                    continue;

                change.MarkRejected();
                if (!reverted.Contains(change.Key, StringComparer.OrdinalIgnoreCase))
                    reverted.Add(change.Key);
            }
        }

        return Task.FromResult(new RevertReport(reverted, Array.Empty<string>()));
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
}
