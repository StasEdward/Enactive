namespace Enactive.Core.Context;

using System.Runtime.InteropServices;

/// <summary>
/// The single place that decides whether a path is inside the workspace. Every tool and every
/// artifact store resolves through here, because three separate copies of "does this string start
/// with the root" is how a check ends up enforced in two of the three.
///
/// It answers two questions the old string comparison could not:
///
/// 1. <b>Links.</b> A junction or symlink INSIDE the workspace can point anywhere. The normalised
///    string still starts with the root, so the check passed while the file operation followed the
///    link out of the folder. Every existing component of the path is now examined, and a reparse
///    point whose final target leaves the root is refused.
/// 2. <b>Case.</b> Comparing with OrdinalIgnoreCase everywhere is wrong on a case-sensitive file
///    system, where <c>/home/x/Work</c> and <c>/home/x/work</c> are two different folders.
///
/// It also reserves <c>.enactive/</c>: that folder holds the workspace's own state, and nothing a
/// model asks for is allowed to write there.
/// </summary>
public static class WorkspaceGuard
{
    /// <summary>The workspace's own state folder. Off limits to tools, whatever the request says.</summary>
    public const string ReservedFolder = ".enactive";

    /// <summary>
    /// Path comparison for the current OS. Linux is case-sensitive; Windows and macOS are not by
    /// default. Getting this backwards either lets a path escape or refuses a legitimate one.
    /// </summary>
    public static StringComparison Comparison { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="root"/> and returns the full
    /// path, or throws <see cref="ArgumentException"/> when it does not stay inside the workspace.
    /// </summary>
    /// <param name="allowReserved">
    /// Lets the app itself write the workspace's own state folder. Never true for a tool call.
    /// </param>
    public static string ResolveInside(string root, string? relativePath, bool allowReserved = false)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root is empty.", nameof(root));

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;

        if (Path.IsPathRooted(relative))
            throw new ArgumentException("Absolute paths are not allowed.", nameof(relativePath));

        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));

        if (!IsInside(fullRoot, full))
            throw new ArgumentException("Path escapes the workspace root.", nameof(relativePath));

        if (!allowReserved && TouchesReserved(fullRoot, full))
            throw new ArgumentException(
                $"'{ReservedFolder}' holds the workspace's own state and is not writable by tools.",
                nameof(relativePath));

        EnsureNoEscapingLink(fullRoot, full);
        return full;
    }

    /// <summary>Whether <paramref name="full"/> is the root itself or something under it.</summary>
    public static bool IsInside(string fullRoot, string full)
    {
        if (string.Equals(full, fullRoot, Comparison))
            return true;

        var withSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return full.StartsWith(withSeparator, Comparison);
    }

    /// <summary>Whether any segment of the path below the root is the reserved state folder.</summary>
    private static bool TouchesReserved(string fullRoot, string full)
    {
        if (string.Equals(full, fullRoot, Comparison))
            return false;

        var relative = Path.GetRelativePath(fullRoot, full);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (string.Equals(segment, ReservedFolder, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Follows every existing component between the root and the target. A component that is a
    /// reparse point (junction, symlink) is resolved to its final target, and that target has to be
    /// inside the workspace too — otherwise the string check passes while the write lands elsewhere.
    /// Components that do not exist yet are skipped: there is nothing to follow, and the write will
    /// create a plain file or folder.
    /// </summary>
    private static void EnsureNoEscapingLink(string fullRoot, string full)
    {
        var components = new List<string>();
        for (var current = full;
             current is not null && !string.Equals(current, fullRoot, Comparison);
             current = Path.GetDirectoryName(current))
        {
            components.Add(current);
        }

        // Deepest last: report the outermost offending link, which is the one the user can see.
        for (var i = components.Count - 1; i >= 0; i--)
        {
            var path = components[i];

            FileSystemInfo info;
            if (Directory.Exists(path)) info = new DirectoryInfo(path);
            else if (File.Exists(path)) info = new FileInfo(path);
            else continue;

            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            string? target;
            try { target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName; }
            catch { target = null; }   // a broken or unreadable link resolves to nothing to follow

            if (target is null)
                continue;

            if (!IsInside(fullRoot, Path.GetFullPath(target)))
                throw new ArgumentException(
                    $"'{Path.GetRelativePath(fullRoot, path)}' is a link that leads outside the workspace.",
                    nameof(full));
        }
    }
}
