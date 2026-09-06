namespace Enactive.Core.Artifacts;

/// <summary>Kind of result an agent produces.</summary>
public enum ArtifactKind { FileSet, Diff, Report, Dashboard, Sql, Config, Preview }

/// <summary>A handle into <see cref="IArtifactStore"/>. Never a physical path (PLAN_v2 §2A.4).</summary>
public sealed record ArtifactRef(Guid Id, ArtifactKind Kind, string Title, string RelativePath);

/// <summary>The result of a task. References storage via <see cref="ArtifactRef"/>.</summary>
public sealed record Artifact(
    Guid Id,
    Guid TaskId,
    ArtifactKind Kind,
    string Title,
    ArtifactRef Ref,
    IReadOnlyList<string> Actions);

/// <summary>
/// Owns where artifact bytes physically live (disk today; git / cloud / remote later).
/// UI and agent layers never learn the location.
/// </summary>
public interface IArtifactStore
{
    Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title,
        Func<Stream, Task> write, CancellationToken ct);

    Task<Stream> OpenAsync(Guid artifactId, CancellationToken ct);

    Task DeleteAsync(Guid artifactId, CancellationToken ct);

    /// <summary>
    /// What this store would serve for a path that has been written but not yet committed to the
    /// workspace, or null when there is nothing pending and the caller should read the disk.
    ///
    /// Staging held its proposals in memory only, so <c>write_file</c> then <c>read_file</c> — the
    /// read-back the standard instructions ask for — returned the OLD content or "File not found",
    /// and the next step of a plan could not see a file the previous step had just "created", while
    /// every message said it was on disk. A store that holds writes has to be able to answer reads.
    /// </summary>
    Task<string?> TryReadPendingAsync(string relativePath, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <summary>Paths this store is holding uncommitted content for. Empty when it writes straight through.</summary>
    IReadOnlyCollection<string> PendingPaths => Array.Empty<string>();

    /// <summary>
    /// Whether the version this store displaced at <paramref name="relativePath"/> can actually be
    /// put back. Asked so a tool can report what is true rather than what is usually true: taking a
    /// backup is best-effort and can fail silently, while <c>write_file</c> told the model — and
    /// therefore the reviewer, which treats that sentence as ground truth — that the previous
    /// version "was kept and can be restored" every single time.
    /// </summary>
    bool CanRestore(string relativePath) => false;

    /// <summary>
    /// Removes a file, recording it the same way a write is recorded so it can be put back.
    ///
    /// Exists because a rename is a write plus a removal, and doing the removal outside the store —
    /// with <c>run_command</c>, as the model had to before — leaves no journal entry, no backup and
    /// nothing for a rejected step to undo. Half a rename inside the safety net is worse than none.
    ///
    /// Throws <see cref="NotSupportedException"/> for a store that cannot express a deletion, which
    /// the caller must report rather than fall back to deleting the file itself.
    /// </summary>
    Task RemoveAsync(string relativePath, CancellationToken ct)
        => throw new NotSupportedException("This artifact store cannot remove files.");

    /// <summary>
    /// Opens a view of this store for one unit of work — a plan step, or a quick action — whose
    /// writes are all attributed to it and which can undo exactly those.
    ///
    /// <para>This replaced a checkpoint id plus a shared "newest scope" field on the store. The
    /// field could not answer the question it was asked: every write took the value the field
    /// happened to hold, not the identity of the step that made it. Two steps sharing one store
    /// need only open in one order and write in the other — A checkpoints, B checkpoints, A writes
    /// its accepted work, B writes work the reviewer then rejects — and BOTH writes carried B's
    /// number, so undoing B destroyed A. A view handed to the step cannot be got wrong that way:
    /// the owner is fixed when the view is made, not read off the store when the write lands.</para>
    /// </summary>
    IArtifactScope BeginStep() => new UnownedScope(this);
}

/// <summary>
/// One unit of work's view of a store. Everything written through it belongs to it, and
/// <see cref="RevertAsync"/> undoes that and nothing else.
/// </summary>
public interface IArtifactScope : IArtifactStore
{
    /// <summary>
    /// Every path this scope has written or removed, as the STORE recorded it at the moment the
    /// operation happened.
    ///
    /// <para>This is the record a revert and a review should be built from, and for a while neither
    /// was. The orchestrator worked out "what did this step write" by parsing the model's own
    /// conversation for <c>write_file</c> calls, which was wrong twice over: <c>edit_file</c> and
    /// <c>move_file</c> do not have that name and so were never rolled back at all, and once the
    /// conversation had to be shortened to fit the context window the arguments it was reading were
    /// gone — so a trimmed run silently stopped undoing anything. What was executed is not something
    /// to reconstruct from a prompt. The store knows.</para>
    /// </summary>
    IReadOnlyCollection<string> TouchedPaths => Array.Empty<string>();

    /// <summary>
    /// Undoes what THIS scope wrote to <paramref name="paths"/>, restoring each one to the content
    /// it had when the scope opened (removing it if it did not exist).
    ///
    /// This is what makes a rejected step mean something. Without it the review gate stopped the
    /// REPORT — the run said Failed — while the consequence stayed on disk: a guide full of invented
    /// command syntax sat in the workspace under a red status. A gate that leaves the damage behind
    /// is only half a gate.
    ///
    /// Only the named paths are touched, and only where this scope is still the last to have
    /// written them. A path another scope has written since is LEFT ALONE and reported back: their
    /// step may already have been accepted, and putting the file back to OUR "before" would destroy
    /// approved work. A hash check cannot catch that — what is on disk matches their write exactly,
    /// so it looks untouched.
    /// </summary>
    Task<RevertReport> RevertAsync(IReadOnlyCollection<string> paths, CancellationToken ct);
}

/// <summary>
/// A store that can attribute a write to an owner, which is what makes real scopes possible. The
/// owner is an opaque id the store hands out; nobody outside interprets it.
/// </summary>
public interface IOwnedArtifactStore : IArtifactStore
{
    /// <summary>A fresh owner id, recorded against the store's current state.</summary>
    int NewOwner();

    Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title,
        Func<Stream, Task> write, int owner, CancellationToken ct);

    Task RemoveAsync(string relativePath, int owner, CancellationToken ct);

    Task<RevertReport> RevertOwnedAsync(
        int owner, IReadOnlyCollection<string> paths, CancellationToken ct);

    /// <summary>Every path this owner has written or removed, as recorded when it happened.</summary>
    IReadOnlyCollection<string> TouchedBy(int owner);
}

/// <summary>
/// The scope view itself, written once here rather than in each store: it only has to remember
/// which owner it is and pass that to every write.
/// </summary>
public sealed class ArtifactScope : IArtifactScope
{
    private readonly IOwnedArtifactStore _store;
    private readonly int _owner;

    public ArtifactScope(IOwnedArtifactStore store, int owner)
    {
        _store = store;
        _owner = owner;
    }

    public Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
        => _store.CreateAsync(relativePath, kind, title, write, _owner, ct);

    public Task RemoveAsync(string relativePath, CancellationToken ct)
        => _store.RemoveAsync(relativePath, _owner, ct);

    public Task<RevertReport> RevertAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
        => _store.RevertOwnedAsync(_owner, paths, ct);

    public IReadOnlyCollection<string> TouchedPaths => _store.TouchedBy(_owner);

    public Task<Stream> OpenAsync(Guid artifactId, CancellationToken ct) => _store.OpenAsync(artifactId, ct);
    public Task DeleteAsync(Guid artifactId, CancellationToken ct) => _store.DeleteAsync(artifactId, ct);
    public Task<string?> TryReadPendingAsync(string relativePath, CancellationToken ct)
        => _store.TryReadPendingAsync(relativePath, ct);
    public IReadOnlyCollection<string> PendingPaths => _store.PendingPaths;
    public bool CanRestore(string relativePath) => _store.CanRestore(relativePath);

    // Steps do not nest, so a scope opened from a scope is a scope on the store beneath it.
    public IArtifactScope BeginStep() => _store.BeginStep();
}

/// <summary>
/// What a store that knows nothing of owners gives back: a pass-through that reverts nothing. Test
/// doubles and read-only stores land here, and they are honest about it — <see cref="RevertAsync"/>
/// returning Empty says "nothing was undone", which is exactly the truth for a store that never
/// recorded who wrote what.
/// </summary>
internal sealed class UnownedScope : IArtifactScope
{
    private readonly IArtifactStore _store;

    public UnownedScope(IArtifactStore store) => _store = store;

    public Task<RevertReport> RevertAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
        => Task.FromResult(RevertReport.Empty);

    public Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
        => _store.CreateAsync(relativePath, kind, title, write, ct);

    public Task<Stream> OpenAsync(Guid artifactId, CancellationToken ct) => _store.OpenAsync(artifactId, ct);
    public Task DeleteAsync(Guid artifactId, CancellationToken ct) => _store.DeleteAsync(artifactId, ct);
    public Task RemoveAsync(string relativePath, CancellationToken ct) => _store.RemoveAsync(relativePath, ct);
    public Task<string?> TryReadPendingAsync(string relativePath, CancellationToken ct)
        => _store.TryReadPendingAsync(relativePath, ct);
    public IReadOnlyCollection<string> PendingPaths => _store.PendingPaths;
    public bool CanRestore(string relativePath) => _store.CanRestore(relativePath);
    public IArtifactScope BeginStep() => this;
}

/// <summary>What a revert actually managed to undo.</summary>
public sealed record RevertReport(
    IReadOnlyList<string> Reverted,
    IReadOnlyList<string> Kept)
{
    public static readonly RevertReport Empty =
        new(Array.Empty<string>(), Array.Empty<string>());

    public bool DidSomething => Reverted.Count > 0 || Kept.Count > 0;
}
