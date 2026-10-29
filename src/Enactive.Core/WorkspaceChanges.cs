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

    /// <summary>
    /// Writes <paramref name="path"/> back as <paramref name="before"/> holds it, where those are exactly the bytes that
    /// were there and the file is still as <paramref name="after"/> saw it; otherwise writes nothing and says why.
    /// <paramref name="changedWithIt"/> is every path changed between the two.
    /// </summary>
    Task<PutBack> PutBackAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, string path,
        IReadOnlyCollection<string> changedWithIt, CancellationToken ct)
        => Task.FromResult(PutBack.Left(PutBackReasons.NotGit));
}

/// <summary>Whether a file was put back, and, where it was not, why - in words a step is told.</summary>
public sealed record PutBack(bool Done, string? Why)
{
    public static PutBack Back { get; } = new(true, null);
    public static PutBack Left(string why) => new(false, why);
}

/// <summary>Why a file was not put back (<see cref="IWorkspaceChanges.PutBackAsync"/>).</summary>
public static class PutBackReasons
{
    public const string NotGit = "not a git workspace";
    public const string Conversion = "a line-ending conversion applies to it";
    public const string ChangedAgain = "it changed again after the command";
    public const string NotAFile = "it is not a regular file";
    public const string Attributes = "the command changed a .gitattributes";
    public const string NotExact = "its bytes could not be written back exactly";
    public const string Settings = "the command changed the repository's own settings (its config or info/attributes)";
}

/// <summary>One moment of the workspace: a git tree id, or the size, write time and (up to a limit) content hash of every file.</summary>
public sealed record WorkspaceSnapshot(string? Tree, IReadOnlyDictionary<string, (long Size, long Ticks, string? Hash)>? Files)
{
    /// <summary>
    /// The repository's own settings as they stood when the tree was made - its config and info/attributes, as a hash. A
    /// blob is the file's bytes only under the settings it was made with; read after a command, settings the command
    /// changed (core.autocrlf set false) answered for a blob made under the old ones (review of PR #65). Null outside git.
    /// </summary>
    public string? Settings { get; init; }
}

public enum FileChangeKind { Added, Modified, Deleted, Renamed }

/// <summary>
/// One file a step changed. <see cref="Diff"/> is a unified diff when one could be made - always in a
/// git work tree for a text file, never outside git - and null otherwise.
/// </summary>
public sealed record FileChange(string Path, FileChangeKind Kind, string? Diff, bool Binary, string? OldPath = null);
