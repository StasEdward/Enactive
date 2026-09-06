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
}
