namespace Enactive.Application.Projections;

/// <summary>What a line of a unified diff does to the file.</summary>
internal enum DiffLineKind { Added, Removed, Context }

/// <summary>
/// A line of a review's diff with its numbers in the file as it was and as it is - none where the line is in only one
/// of them, or in neither (the heading, the blank line under it).
/// </summary>
internal sealed record NumberedDiffLine(string Text, DiffLineKind Kind, int? Old, int? New);

/// <summary>
/// A line of a unified diff, by what it does to the file - the colour it is drawn in on both hosts. Moved from the
/// window (DiffLineViewModel) so the service's diff colours the same lines: the rule is one character, and two copies
/// of it drift the day one of them learns that "+++" heads a file rather than adding a line.
/// </summary>
internal static class DiffLines
{
    public static DiffLineKind Kind(string line)
        => line.StartsWith('+') ? DiffLineKind.Added
            : line.StartsWith('-') ? DiffLineKind.Removed
            : DiffLineKind.Context;

    /// <summary>
    /// The diff's lines, each with its numbers, as LineDiff writes them: "  " is in both files, "- " only in the old,
    /// "+ " only in the new; the "# " heading and anything else - the blank line under it, a too-large notice - has none.
    /// Here and not in each host's view, for the reason <see cref="Kind"/> is: the service's review and the window's
    /// numbered the same lines in two copies of one rule.
    /// </summary>
    public static IReadOnlyList<NumberedDiffLine> Numbered(string diff)
    {
        var (was, now) = (0, 0);
        var lines = new List<NumberedDiffLine>();
        foreach (var line in Split(diff))
        {
            var (old, @new) = line.Length >= 2 ? line[..2] switch
            {
                "  " => ((int?)++was, (int?)++now),
                "- " => (++was, null),
                "+ " => (null, ++now),
                _ => ((int?)null, (int?)null)
            } : (null, null);
            lines.Add(new NumberedDiffLine(line, Kind(line), old, @new));
        }
        return lines;
    }

    /// <summary>The diff's lines, whichever line ends it was written with, without the empty one after the last.</summary>
    public static string[] Split(string diff) => diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
