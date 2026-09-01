namespace AIClient.Core.Artifacts;

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
}
