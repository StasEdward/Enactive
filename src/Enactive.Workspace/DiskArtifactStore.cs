namespace Enactive.Workspace;

using System.Collections.Concurrent;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;

/// <summary>
/// Disk-backed artifact store rooted at the workspace. The rest of the app addresses artifacts by
/// id / <see cref="ArtifactRef"/> and never learns the physical path (PLAN_v2 §2A.4).
/// </summary>
public sealed class DiskArtifactStore : IArtifactStore
{
    private readonly string _root;
    private readonly ConcurrentDictionary<Guid, string> _paths = new();

    /// <summary>
    /// Relative path -> did this store CREATE the file, or overwrite one that was already there.
    /// Recorded at the first write, because afterwards the answer is unknowable: the file exists
    /// either way. It is what lets the UI say "delete the file this run created" instead of offering
    /// an "Undo" that cannot restore anything (code review finding #6). Full undo — keeping the old
    /// bytes and comparing hashes — is a separate change; this only stops the false promise.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _createdHere = new(StringComparer.OrdinalIgnoreCase);

    public DiskArtifactStore(WorkspaceInfo workspace)
        => _root = Path.GetFullPath(workspace.RootPath);

    public string Root => _root;

    /// <summary>
    /// True when this store created the file rather than overwriting an existing one. False for a
    /// path it never wrote, so the caller can only ever be told LESS than it is safe to delete.
    /// </summary>
    public bool CreatedHere(string relativePath)
        => _createdHere.TryGetValue(relativePath, out var created) && created;

    public async Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
    {
        var fullPath = ResolveInsideRoot(relativePath);

        // Only the FIRST write decides: a second write in the same run overwrites what the first one
        // produced, which does not make a pre-existing file ours to delete.
        _createdHere.GetOrAdd(relativePath, _ => !File.Exists(fullPath));

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await write(stream);

        var id = Guid.NewGuid();
        _paths[id] = fullPath;
        return new ArtifactRef(id, kind, title, relativePath);
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

    private string ResolveInsideRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is empty.", nameof(relativePath));
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Absolute paths are not allowed.", nameof(relativePath));

        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path escapes the workspace root.", nameof(relativePath));

        return fullPath;
    }
}
