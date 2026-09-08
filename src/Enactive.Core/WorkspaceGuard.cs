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
    /// <summary>
    /// The one name a file has, for anything that keys records BY file: the path relative to the
    /// workspace root, with '/' separators, taken from the resolved full path rather than from what
    /// the caller typed.
    ///
    /// <para>Callers spell one file many ways — <c>doc.txt</c>, <c>./doc.txt</c>, <c>a/b.txt</c> and
    /// <c>a\b.txt</c> — and a model spells it differently in two consecutive tool calls as a matter
    /// of course. The write journal keyed its entries by the string it was handed, so one file
    /// became two records: a revert asked about one spelling never saw the other step's write under
    /// the other, and deleted a file that was no longer its own. Guarding a path and IDENTIFYING one
    /// are different jobs; this is the second.</para>
    ///
    /// <para>Compare with <see cref="Comparison"/>, which folds case exactly where the filesystem
    /// does.</para>
    /// </summary>
    public static string KeyFor(string root, string fullPath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative.Replace('\\', '/');
    }

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

        // Both remaining questions are asked of the EFFECTIVE path — where the write actually lands
        // once every link on the way has been followed — not of the string the caller typed. Asking
        // them of the string is how a junction named `alias` pointing at `.enactive` passed the
        // reserved-folder check: `alias/state.txt` contains no reserved segment, and the write then
        // landed in the workspace's own state, backups of the undo journal included.
        var effective = FollowLinks(fullRoot, full, nameof(relativePath));

        if (!IsInside(fullRoot, effective))
            throw new ArgumentException("Path escapes the workspace root.", nameof(relativePath));

        if (!allowReserved && TouchesReserved(fullRoot, effective))
            throw new ArgumentException(
                $"'{ReservedFolder}' holds the workspace's own state and is not writable by tools.",
                nameof(relativePath));

        // The literal path is what the caller opens; the OS follows the same links we just did, so
        // it reaches the destination we vetted. Returning it keeps recorded paths as the user wrote
        // them. NOTE this is a check, not a lock: between here and the open, the path could change.
        // Closing that window needs the open itself to be link-aware, which is a separate change.
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
    /// Walks the path from the root down, one segment at a time, and returns where it really ends
    /// up. A segment that is a reparse point (junction, symlink) is replaced by its final target and
    /// the rest of the path is appended to THAT, so the caller is left holding the location the file
    /// system would actually reach rather than the spelling it was given.
    ///
    /// A link is refused the moment its target leaves the workspace, so the offending segment can be
    /// named — reporting the whole resolved path would point at a folder the user never mentioned.
    /// Segments that do not exist yet are passed through: there is nothing to follow, and the write
    /// will create a plain file or folder.
    /// </summary>
    private static string FollowLinks(string fullRoot, string full, string parameterName)
    {
        if (string.Equals(full, fullRoot, Comparison))
            return fullRoot;

        var segments = Path.GetRelativePath(fullRoot, full)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var effective = fullRoot;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
                continue;

            effective = Path.GetFullPath(Path.Combine(effective, segment));

            FileSystemInfo info;
            if (Directory.Exists(effective)) info = new DirectoryInfo(effective);
            else if (File.Exists(effective)) info = new FileInfo(effective);
            else continue;

            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            string? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (Exception ex)
            {
                // We could not learn where this leads, so we cannot say it is safe. A failure here
                // is a refusal, not a shrug: the old code caught this and carried on, which meant an
                // unreadable link was treated exactly like an ordinary folder.
                throw new ArgumentException(
                    $"'{Path.GetRelativePath(fullRoot, effective)}' is a link that could not be "
                    + $"resolved ({ex.Message}), so where it leads is unknown.", parameterName);
            }

            // Null is not a failure: a cloud-storage placeholder carries the reparse attribute
            // without being a link to anywhere, and a link with no target reaches nothing to write.
            if (target is null)
                continue;

            effective = Path.GetFullPath(target);

            if (!IsInside(fullRoot, effective))
                throw new ArgumentException(
                    $"'{Path.GetRelativePath(fullRoot, full)}' passes through a link that leads "
                    + "outside the workspace.", parameterName);
        }

        return effective;
    }
}
