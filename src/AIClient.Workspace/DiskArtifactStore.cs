namespace AIClient.Workspace;

using System.Collections.Concurrent;
using AIClient.Core.Artifacts;
using AIClient.Core.Context;

/// <summary>
/// Disk-backed artifact store rooted at the workspace. The rest of the app addresses artifacts by
/// id / <see cref="ArtifactRef"/> and never learns the physical path (PLAN_v2 §2A.4).
/// </summary>
public sealed class DiskArtifactStore : IArtifactStore
{
    private readonly string _root;
    private readonly ConcurrentDictionary<Guid, string> _paths = new();

    public DiskArtifactStore(WorkspaceInfo workspace)
        => _root = Path.GetFullPath(workspace.RootPath);

    public string Root => _root;

    public async Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
    {
        var fullPath = ResolveInsideRoot(relativePath);
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
