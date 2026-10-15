namespace Enactive.Application.Projections;

/// <summary>What a line of a unified diff does to the file.</summary>
internal enum DiffLineKind { Added, Removed, Context }

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
}
