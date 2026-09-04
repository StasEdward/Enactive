namespace Enactive.Tools;

/// <summary>Resolves a relative path inside the workspace root, refusing anything that escapes it.</summary>
public static class WorkspacePaths
{
    public static string ResolveInside(string root, string? relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;

        if (Path.IsPathRooted(relative))
            throw new ArgumentException("Absolute paths are not allowed.", nameof(relativePath));

        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path escapes the workspace root.", nameof(relativePath));
        }

        return full;
    }
}
