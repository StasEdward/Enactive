namespace Enactive.Workspace;

using System.Security.Cryptography;
using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;

/// <summary>A proposed file change held for review (not yet written to disk).</summary>
public sealed class StagedChange
{
    public StagedChange(Guid id, string relativePath, string? oldContent, string newContent, int sequence)
    {
        Id = id;
        RelativePath = relativePath;
        OldContent = oldContent;
        NewContent = newContent;
        Sequence = sequence;
        BaseHash = FileHash.Of(oldContent);
    }

    public Guid Id { get; }
    public string RelativePath { get; }
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
                               .Select(c => c.RelativePath)
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

        // The base for the diff and for the conflict check is what a reader would see NOW: an earlier
        // pending proposal for the same path, else the file on disk. Diffing against the disk while a
        // proposal is already outstanding shows a change the user never made.
        var pending = await TryReadPendingAsync(relativePath, ct);
        var oldContent = pending ?? (File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : null);

        var id = Guid.NewGuid();
        lock (_gate)
            _changes.Add(new StagedChange(id, relativePath, oldContent, newContent, _sequence++));

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
        lock (_gate)
        {
            for (var i = _changes.Count - 1; i >= 0; i--)
            {
                var change = _changes[i];
                if (change.Pending
                    && string.Equals(change.RelativePath, relativePath, WorkspaceGuard.Comparison))
                    return Task.FromResult<string?>(change.NewContent);
            }
        }

        return Task.FromResult<string?>(null);
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
                && string.Equals(c.RelativePath, change.RelativePath, WorkspaceGuard.Comparison));

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
            var temp = full + ".enactive-tmp";
            File.WriteAllText(temp, change.NewContent);
            File.Move(temp, full, overwrite: true);
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

    /// <summary>The shared rule — see the same note on <see cref="DiskArtifactStore"/>.</summary>
    private string ResolveInside(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is empty.", nameof(relativePath));

        return WorkspaceGuard.ResolveInside(_root, relativePath);
    }
}
