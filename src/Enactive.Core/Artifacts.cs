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
    /// A marker for "the state the workspace is in right now", to be handed back to
    /// <see cref="RevertToAsync"/>. Opaque on purpose: what it counts is the store's business.
    /// </summary>
    int Checkpoint() => 0;

    /// <summary>
    /// Undoes what was written to <paramref name="paths"/> since <paramref name="checkpoint"/>,
    /// restoring each one to the content it had at that moment (removing it if it did not exist).
    ///
    /// This is what makes a rejected step mean something. Without it the review gate stopped the
    /// REPORT — the run said Failed — while the consequence stayed on disk: a guide full of invented
    /// command syntax sat in the workspace under a red status. A gate that leaves the damage behind
    /// is only half a gate.
    ///
    /// Only the named paths are touched, because several steps may share one store and a step must
    /// never undo a sibling's work. A file that has changed since the step wrote it is LEFT ALONE
    /// and reported back: at that point someone else's edit is in there, and discarding it would be
    /// the very thing this is meant to prevent.
    /// </summary>
    Task<RevertReport> RevertToAsync(int checkpoint, IReadOnlyCollection<string> paths, CancellationToken ct)
        => Task.FromResult(RevertReport.Empty);
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
