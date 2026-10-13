namespace Enactive.Server.Runs;

/// <summary>
/// The folders under which the service runs tasks for the people in its browser (server.json <c>WorkspaceRoots</c>,
/// ADR 0002 §10: "a workspace is a folder on the server, chosen with a folder browser of the server's disks, limited
/// to configured roots").
///
/// <para>An Operator is trusted (ADR 0002, trust model), and still: a box to type any path into made every folder of
/// the server - its profiles, its system directories - one Run away from an agent with a shell. The roots name where
/// the work is; a folder outside them, or reached through a junction or link that leads out of them, is not one.</para>
/// </summary>
internal sealed class WorkspaceRoots(IReadOnlyList<string> roots)
{
    private readonly IReadOnlyList<string> _roots = roots
        .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
        .ToList();

    public IReadOnlyList<string> All => _roots;

    /// <summary>Why the service does not run tasks in this folder, or null when it does.</summary>
    public string? Refusal(string folder)
    {
        if (_roots.Count == 0)
            return "The service runs tasks only in folders under its WorkspaceRoots (server.json), and it names none.";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (_roots.FirstOrDefault(r => Inside(full, r)) is not { } root)
            return $"{full} is not under the folders the service runs tasks in: {string.Join(", ", _roots)}.";
        if (LinkOnTheWay(root, full) is { } link)
            return $"{link} is a link to somewhere else; the service runs tasks only in folders that are under its roots in fact.";
        return null;
    }

    /// <summary>The folders directly in <paramref name="folder"/> that tasks may run in - no links, nothing outside the roots.</summary>
    public IReadOnlyList<string> Below(string folder)
    {
        if (Refusal(folder) is not null || !Directory.Exists(folder))
            return [];
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint) && !d.Attributes.HasFlag(FileAttributes.System))
                .Select(d => d.FullName)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];   // a folder the service's account may not list has nothing to offer
        }
    }

    /// <summary>The folder above this one when tasks may run there, or null: up from a root is the list of roots.</summary>
    public string? Up(string folder)
        => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))) is { } parent && Refusal(parent) is null
            ? parent
            : null;

    // A drive keeps its separator when trimmed ("D:\"): one more made "D:\\", which no folder on the drive starts with,
    // and a drive named as a root refused every folder on it.
    private static bool Inside(string full, string root)
        => string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
           || full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);

    private static string? LinkOnTheWay(string root, string full)
    {
        for (var at = full; at.Length > root.Length; at = Path.GetDirectoryName(at) ?? root)
            if (Directory.Exists(at) && File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint))
                return at;
        return null;
    }
}
