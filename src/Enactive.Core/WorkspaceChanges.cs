namespace Enactive.Core.Artifacts;

/// <summary>Creates an independent snapshot session for a workspace; the caller owns its lifetime.</summary>
public interface IWorkspaceChangesFactory
{
    IWorkspaceChanges Create(string root);
}

/// <summary>Measures workspace state and differences, independent of the storage implementation.</summary>
public interface IWorkspaceChanges : IDisposable
{
    /// <summary>The current snapshot, or null when it cannot be measured.</summary>
    Task<WorkspaceSnapshot?> TakeAsync(CancellationToken ct);
    /// <summary>Paths actually measured by a snapshot, or null when unavailable.</summary>
    Task<IReadOnlySet<string>?> PathsAsync(WorkspaceSnapshot snapshot, CancellationToken ct);
    /// <summary>Changes between snapshots, or null when they cannot be compared.</summary>
    Task<IReadOnlyList<FileChange>?> CompareAsync(
        WorkspaceSnapshot before, WorkspaceSnapshot after, CancellationToken ct);

    /// <summary>Names/kinds only, without materialising patches. Includes changes made outside the artifact store.</summary>
    Task<IReadOnlyList<FileChange>?> ComparePathsAsync(
        WorkspaceSnapshot before, WorkspaceSnapshot after, CancellationToken ct)
        => CompareAsync(before, after, ct);
}

/// <summary>One moment of the workspace: a git tree id, or the size, write time and (up to a limit) content hash of every file.</summary>
public sealed record WorkspaceSnapshot(string? Tree, IReadOnlyDictionary<string, (long Size, long Ticks, string? Hash)>? Files);

public enum FileChangeKind { Added, Modified, Deleted, Renamed }

/// <summary>
/// One file a step changed. <see cref="Diff"/> is a unified diff when one could be made - always in a
/// git work tree for a text file, never outside git - and null otherwise.
/// </summary>
public sealed record FileChange(string Path, FileChangeKind Kind, string? Diff, bool Binary, string? OldPath = null);
