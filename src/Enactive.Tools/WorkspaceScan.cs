namespace Enactive.Tools;

using Enactive.Core.Context;

/// <summary>
/// Which files a workspace-wide tool looks at, and how it decides a file is not text.
///
/// <para>These were private methods inside <see cref="SearchFilesTool"/>. They are here because
/// the counting tools — <c>count_matches</c>, <c>file_stats</c> — have to walk the workspace the
/// SAME way: a count that includes <c>bin/</c> and a search that excludes it disagree about what
/// the workspace IS, and the model has no way to tell which of them is lying. One skip list, in
/// one place, is the only way that stays true when the next tool is added.</para>
/// </summary>
internal static class WorkspaceScan
{
    /// <summary>A file over this is not scanned by content. Reported, never skipped silently.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The places nobody means to search: the workspace's own state folder, and the build and
    /// dependency trees that would otherwise supply thousands of hits from code the user did not
    /// write.
    /// </summary>
    private static readonly string[] SkippedFolders =
        { WorkspaceGuard.ReservedFolder, "bin", "obj", "node_modules", ".git", ".vs", "dist", "packages" };

    /// <summary>
    /// The glob as <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/> will
    /// take it, or an <see cref="ArgumentException"/> saying why it will not.
    ///
    /// <para><b>.NET's searchPattern matches a FILE NAME and forbids a directory separator.</b> A
    /// model does not know that and writes the glob every other tool in the world takes: a
    /// <c>**/*.md</c> comes back as "The filename, directory name, or volume label syntax is
    /// incorrect" — a Windows path error, about a path nobody typed.</para>
    ///
    /// <para>A leading <c>**/</c> is not rejected but REMOVED, because with
    /// <c>RecurseSubdirectories</c> it already means what the caller wants — every folder below the
    /// root. A separator anywhere else names a folder, which is what the <c>path</c> argument is
    /// for, and that gets an error that SAYS so instead of one from the filesystem.</para>
    /// </summary>
    public static string FileNamePattern(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return "*";

        var pattern = glob.Replace('\\', '/').Trim();

        while (true)
        {
            if (pattern.StartsWith("./", StringComparison.Ordinal)) { pattern = pattern[2..]; continue; }
            if (pattern.StartsWith("**/", StringComparison.Ordinal)) { pattern = pattern[3..]; continue; }
            break;
        }

        if (pattern.Length == 0 || pattern == "**")
            return "*";

        if (pattern.Contains('/'))
            throw new ArgumentException(
                $"'{glob}' names a path. This argument matches FILE NAMES only — \"*.md\", \"*.cs\" — "
              + "and the scan already covers every folder below the root, so no \"**/\" is needed. "
              + "To look inside one folder, pass it as 'path' instead.");

        return pattern;
    }

    /// <summary>Files under <paramref name="root"/> matching <paramref name="glob"/>, skip list applied.</summary>
    public static IEnumerable<string> Files(string root, string? glob)
        // Validated HERE rather than inside the iterator below: an exception thrown from a yield
        // method surfaces on the first MoveNext, which is inside the caller's scan loop, where it
        // reads as the scan having crashed halfway rather than as an argument it can fix.
        => Walk(root, FileNamePattern(glob));

    private static IEnumerable<string> Walk(string root, string glob)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint   // a link is followed by nothing here
        };

        foreach (var file in Directory.EnumerateFiles(root, glob, options))
        {
            if (!Skipped(root, file))
                yield return file;
        }
    }

    /// <summary>
    /// Whether this file sits in one of the skipped folders, asked of the path BELOW the scan root
    /// rather than of the whole path.
    ///
    /// <para>Splitting the absolute path made those names mean two things they were never meant to
    /// mean:</para>
    /// <list type="number">
    /// <item><b>A workspace whose own location contains one of them was unsearchable.</b> A project
    /// under <c>C:\dev\packages\thing</c> matched on a segment of its own address, so every file
    /// was skipped and every search answered "No matches" — a wrong answer stated as a fact.</item>
    /// <item><b>Pointing a scan AT a skipped folder could not work.</b> The worker's scratch area is
    /// under <c>.enactive/</c>, so a scan rooted there matched on the root's own segment and
    /// returned nothing, always. Counting from the root gives what a person expects from every
    /// other tool of this kind: the noisy places are left out of a sweep and looked in when you
    /// name them. That is the whole of how scratch is reachable.</item>
    /// </list>
    /// </summary>
    public static bool Skipped(string scanRoot, string file)
    {
        var relative = Path.GetRelativePath(scanRoot, file);

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            foreach (var skip in SkippedFolders)
                if (string.Equals(segment, skip, WorkspaceGuard.Comparison))
                    return true;
        return false;
    }

    /// <summary>
    /// A NUL byte in the first few KB means this is not text. Cheap, and wrong only for files that
    /// would be unreadable in the output anyway.
    /// </summary>
    public static bool Binary(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[Math.Min(4096, (int)Math.Max(1, stream.Length))];
            var read = stream.Read(head);
            return head[..read].IndexOf((byte)0) >= 0;
        }
        catch { return true; }   // unreadable is as good as binary for this purpose
    }

    public static string Relative(string root, string file)
        => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// What a scan did not look inside, as a sentence for the result.
    ///
    /// <para>The skips used to be a <c>continue</c> and nothing else, so "no matches in 40 file(s)"
    /// was a sentence about 40 files presented as a fact about the workspace. A 3 MB generated file
    /// is exactly the kind that holds the string somebody is looking for.</para>
    /// </summary>
    public static string SkippedNote(int large, int binary)
    {
        if (large == 0 && binary == 0)
            return "";

        var parts = new List<string>(2);
        if (large > 0)
            parts.Add($"{large} file(s) larger than {MaxFileBytes / (1024 * 1024)} MB");
        if (binary > 0)
            parts.Add($"{binary} binary file(s)");

        return $"\n… not scanned: {string.Join(" and ", parts)}. "
             + "Read one directly with read_file if the answer might be in it.";
    }
}
