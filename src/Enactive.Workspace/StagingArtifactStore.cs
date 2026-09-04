namespace Enactive.Workspace;

using System.Text;
using Enactive.Core.Artifacts;

/// <summary>A proposed file change held for review (not yet written to disk).</summary>
public sealed class StagedChange
{
    public StagedChange(Guid id, string relativePath, string? oldContent, string newContent)
    {
        Id = id;
        RelativePath = relativePath;
        OldContent = oldContent;
        NewContent = newContent;
    }

    public Guid Id { get; }
    public string RelativePath { get; }
    public string? OldContent { get; }
    public string NewContent { get; }
    public bool IsNew => OldContent is null;
    public bool Applied { get; private set; }
    public bool Rejected { get; private set; }

    internal void MarkApplied() => Applied = true;
    internal void MarkRejected() => Rejected = true;
}

/// <summary>
/// An <see cref="IArtifactStore"/> that stages writes instead of committing them: the proposed content
/// is captured (with the previous content for a diff) and only written to disk on <see cref="Apply"/>.
/// Lets the UI show a diff with Apply/Reject before anything touches the workspace.
/// </summary>
public sealed class StagingArtifactStore : IArtifactStore
{
    private readonly string _root;
    private readonly List<StagedChange> _changes = new();

    public StagingArtifactStore(string workspaceRoot) => _root = Path.GetFullPath(workspaceRoot);

    public IReadOnlyList<StagedChange> Changes => _changes;

    public async Task<ArtifactRef> CreateAsync(
        string relativePath, ArtifactKind kind, string title, Func<Stream, Task> write, CancellationToken ct)
    {
        // Capture the proposed content without touching disk.
        await using var buffer = new MemoryStream();
        await write(buffer);
        var newContent = Encoding.UTF8.GetString(buffer.ToArray());

        var full = ResolveInside(relativePath);
        var oldContent = File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : null;

        var id = Guid.NewGuid();
        _changes.Add(new StagedChange(id, relativePath, oldContent, newContent));
        return new ArtifactRef(id, kind, title, relativePath);
    }

    public Task<Stream> OpenAsync(Guid artifactId, CancellationToken ct)
    {
        var change = _changes.Find(c => c.Id == artifactId)
                     ?? throw new FileNotFoundException("Unknown staged artifact.", artifactId.ToString());
        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(change.NewContent));
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid artifactId, CancellationToken ct)
    {
        Reject(artifactId);
        return Task.CompletedTask;
    }

    /// <summary>Commit a staged change to disk.</summary>
    public void Apply(Guid id)
    {
        var change = _changes.Find(c => c.Id == id);
        if (change is null || change.Applied || change.Rejected)
            return;

        var full = ResolveInside(change.RelativePath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(full, change.NewContent);
        change.MarkApplied();
    }

    /// <summary>Discard a staged change.</summary>
    public void Reject(Guid id) => _changes.Find(c => c.Id == id)?.MarkRejected();

    private string ResolveInside(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is empty.", nameof(relativePath));
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Absolute paths are not allowed.", nameof(relativePath));

        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path escapes the workspace root.", nameof(relativePath));

        return full;
    }
}
