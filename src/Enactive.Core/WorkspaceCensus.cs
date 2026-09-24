namespace Enactive.Core.Context;

/// <summary>
/// How much of what there is, by folder — the one fact about a workspace a planner cannot obtain
/// and cannot do without.
///
/// <para><b>What it is for.</b> A plan is written by a model that has never seen the project. Asked
/// to "check every page", it writes one step — which is right for three pages and fatal for three
/// hundred, and it has no way to tell which it is looking at. Measured 2026-09-22, the same request
/// on the same workspace five times: the planner produced 1, 3, 4 and 5 steps on different runs,
/// and the single-step version ran to the 250-turn backstop, was marked Incomplete and took the
/// rest of the plan with it. That is not a judgement the planner got wrong; it is a number nobody
/// gave it.</para>
///
/// <para><b>Why counting is the fix and not more instruction.</b> The engine already tells the
/// planner what a step costs and when one is abandoned. That is advice about a quantity it still
/// cannot see. A census is the quantity: "Docs/wiki 11 .md" turns "check every page" into
/// arithmetic the planner can do in its head.</para>
///
/// <para><b>Two levels deep, and no deeper.</b> The folders people name in a request are the
/// top two — <c>src</c>, <c>Docs/wiki</c>, <c>tests</c> — and every level after that multiplies the
/// lines without changing a plan. The whole block is a few hundred characters, sent once per run,
/// at the front of a prompt that is cached.</para>
///
/// <para>Skips what <see cref="WorkspaceGuard.SkippedFolders"/> skips, because a count that
/// includes <c>bin/</c> and a search that excludes it disagree about what the workspace IS, and
/// nothing downstream can tell which of them is lying.</para>
/// </summary>
public static class WorkspaceCensus
{
    /// <summary>Folders listed, largest first. Past this the block is noise, not information.</summary>
    public const int MaxFolders = 12;

    /// <summary>Extensions named per folder. The tail is "and 14 other kinds", which is enough.</summary>
    public const int MaxExtensions = 3;

    /// <summary>
    /// A folder with fewer files than this is not worth a line of its own: it is a handful of
    /// loose files, and the planner does not size work against it.
    /// </summary>
    public const int MinFiles = 3;

    /// <summary>
    /// Files looked at before the walk gives up. A census is a courtesy to the planner and must
    /// never be the reason a run takes a noticeable moment to start; a repository large enough to
    /// reach this has already told the planner everything the first 40,000 files could.
    /// </summary>
    public const int MaxFilesScanned = 40_000;

    /// <summary>
    /// The census of a KNOWN list of workspace-relative paths — the honest input, and the one this
    /// is tested against.
    ///
    /// <para><b>Why a list and not a walk.</b> Run against this repository, a walk of the disk
    /// reported <c>work — 417 .ps1, 210 .log</c> and four folders of <c>.dll</c> before it reached
    /// any source: scratch piles, publish output and restored packages are most of what is on disk
    /// and none of what anybody means by "the project". A planner sizing steps against that number
    /// is worse off than one given nothing. What a person means is what the repository tracks, so
    /// the caller hands in <c>git ls-files</c> and falls back to a walk only where there is no
    /// repository to ask.</para>
    /// </summary>
    /// <param name="cutAfter">
    /// How many files the caller managed to look at before it gave up, when it did. Passed rather
    /// than assumed so the note says the real number and not the constant.
    /// </param>
    public static IReadOnlyList<string> Of(IEnumerable<string> relativePaths, int? cutAfter = null)
    {
        var folders = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in relativePaths)
        {
            var relative = raw.Replace('\\', '/').Trim();
            if (relative.Length == 0 || Skipped(relative))
                continue;

            var folder = FolderOf(relative);
            var extension = Path.GetExtension(relative);
            if (extension.Length == 0)
                extension = "(no extension)";

            if (!folders.TryGetValue(folder, out var kinds))
                folders[folder] = kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            kinds[extension] = kinds.TryGetValue(extension, out var n) ? n + 1 : 1;
        }

        var lines = folders
            .Where(f => f.Value.Values.Sum() >= MinFiles)
            .OrderByDescending(f => f.Value.Values.Sum())
            .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFolders)
            .Select(f => Line(f.Key, f.Value))
            .ToList();

        // Said, not silently applied. A census that stopped counting and did not mention it is a
        // number presented as the whole truth, which is the one thing a planner must not be given.
        if (cutAfter is { } counted && lines.Count > 0)
            lines.Add($"(counted the first {counted:N0} files; there are more)");

        return lines;
    }

    /// <summary>
    /// The lines to put in front of the planner, or an empty list when there is nothing to say.
    /// Never throws: a workspace that cannot be walked is a workspace the planner is told nothing
    /// about, which is exactly where it was before this existed.
    /// </summary>
    /// <param name="maxFiles">
    /// Overridable so the cap can be DRIVEN by a test rather than described by one. A limit nobody
    /// has crossed on purpose is a limit nobody knows the behaviour of.
    /// </param>
    public static IReadOnlyList<string> Of(string? root, int maxFiles = MaxFilesScanned)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<string>();

        var found = new List<string>();
        var scanned = 0;
        var cut = false;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (++scanned > maxFiles)
                {
                    cut = true;
                    break;
                }

                found.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        return Of(found, cut ? maxFiles : null);
    }

    /// <summary>"src/Enactive.Core 41 .cs, 1 .csproj" — the folder, then what is in it.</summary>
    private static string Line(string folder, Dictionary<string, int> kinds)
    {
        var named = kinds.OrderByDescending(k => k.Value)
                         .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxExtensions)
                         .Select(k => $"{k.Value} {k.Key}")
                         .ToArray();

        var rest = kinds.Count - named.Length;
        var tail = rest > 0 ? $", and {rest} other kind(s)" : "";

        return $"{folder} — {string.Join(", ", named)}{tail}";
    }

    /// <summary>
    /// The first two segments of a path, which is as deep as this looks: "Docs/wiki" for a page,
    /// "src" for a file loose in it. A file in the root is counted under ".".
    /// </summary>
    private static string FolderOf(string relative)
    {
        var parts = relative.Split('/');

        return parts.Length switch
        {
            <= 1 => ".",
            2 => parts[0],
            _ => $"{parts[0]}/{parts[1]}"
        };
    }

    private static bool Skipped(string relative)
    {
        foreach (var segment in relative.Split('/'))
            foreach (var skip in WorkspaceGuard.SkippedFolders)
                if (string.Equals(segment, skip, WorkspaceGuard.Comparison))
                    return true;

        return false;
    }
}
