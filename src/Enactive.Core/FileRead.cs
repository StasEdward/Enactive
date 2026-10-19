namespace Enactive.Core.Execution;

using System.Text;

/// <summary>
/// What a read call's result holds of one file it read: which file, how many lines it has, and where in the result its
/// text is - so the step reviewer, shown that result cut to its share of the evidence, is told the file was read and which
/// of its lines it is shown.
///
/// <para><b>Why.</b> Run 2eedd5, 2026-10-20, step 1: the worker read five test files in one <c>read_files</c> call. The
/// reviewer was shown the call as its arguments compacted to 120 characters - the first two paths and part of a third -
/// and its result cut to its start and its end, which held the first file's opening lines and a note appended after the
/// last. It failed the step because two of the files "were never read in any shown call". They were; the cut hid their
/// names, and nothing it was shown told "read, not shown here" from "not read". Which files a call read is what a reviewer
/// checks a report's claims against, and naming them costs a line.</para>
///
/// <para>Made by the tool (<see cref="MetadataKey"/>), the one place that knows where each file's text sits in what it
/// returned, and carried by the journal beside the call (<see cref="ExecutedAction.FilesRead"/>). The model's transcript is
/// as it was: only the reviewer's evidence presents a read this way (ExecutionJournal.Describe).</para>
/// </summary>
/// <param name="Path">The file, as the call named it.</param>
/// <param name="TotalLines">Its line count - one more than its line breaks; when <paramref name="TotalKnown"/> is false,
/// only the count in the part the tool scanned.</param>
/// <param name="Spans">Where the file's own text is in the result, in order: one stretch for a file or a window shown
/// whole, two for a file shown as its start and its end.</param>
public sealed record FileRead(string Path, int TotalLines, bool TotalKnown, IReadOnlyList<FileRead.Span> Spans)
{
    /// <summary>The key a tool's result carries the files it read under: an <c>IReadOnlyList&lt;FileRead&gt;</c>.</summary>
    public const string MetadataKey = "filesRead";

    /// <summary>
    /// The most of a call line spent naming the files a call read. Generous on purpose - read_files takes at most ten
    /// paths, and the names are what a reviewer checks "was it read" against - and a bound all the same, past which the
    /// rest are counted rather than named.
    /// </summary>
    public const int MaxPathsChars = 1_000;

    /// <summary>
    /// One stretch of the result that is the file's own text: <paramref name="Length"/> characters from
    /// <paramref name="Start"/>, beginning in line <paramref name="FirstLine"/> and ending in line <paramref name="LastLine"/>.
    /// </summary>
    /// <param name="StartsLine">Whether the stretch begins where its first line does - false for the end of a file shown
    /// as its start and its end, which begins wherever that cut fell.</param>
    /// <param name="EndsLine">Whether its last line is whole in it: the file goes on with a line break there, or ends, or
    /// all that follows of the line is blank.</param>
    public sealed record Span(int Start, int Length, int FirstLine, int LastLine, bool StartsLine, bool EndsLine);

    /// <summary>One line, for the call it belongs to: the file it read, or how many and each by name.</summary>
    public static string Summary(IReadOnlyList<FileRead> files)
    {
        if (files.Count == 1) return files[0].Path;
        var sb = new StringBuilder($"{Invariant.N0(files.Count)} files: ");
        for (var k = 0; k < files.Count; k++)
        {
            var part = (k > 0 ? ", " : "") + files[k].Path;
            // The first is named whatever its length: a call line that names no file is the defect this is here to end.
            if (k > 0 && sb.Length + part.Length > MaxPathsChars)
                return sb.Append($", and {Invariant.N0(files.Count - k)} more not named here").ToString();
            sb.Append(part);
        }
        return sb.ToString();
    }

    /// <summary>What a shortened result says above its lines of what is shown of each file.</summary>
    private const string Lead = "Shown here of the files this call read:";

    /// <summary>
    /// Which lines of each file are shown whole in <paramref name="result"/> when only <paramref name="kept"/> of it is
    /// shown: a line under the result per file, said of the FILE - "lines 1-40 of 264" - so a file of which nothing is shown
    /// reads as read and not shown, not as never read.
    /// </summary>
    /// <param name="kept">The stretches of <paramref name="result"/> that are shown, as [start, end) - its start and its
    /// end when it is cut (Shortening.HeadAndTail).</param>
    public static string Shown(IReadOnlyList<FileRead> files, string result, IReadOnlyList<(int Start, int End)> kept)
        => Lead + string.Concat(files.Select(f => "\n  " + f.Line(f.LinesShown(result, kept))));

    /// <summary>
    /// The most <see cref="Shown"/> can take for a result kept as at most two stretches - its start and its end - each
    /// number at its widest: the room a caller keeps for it, so the evidence stays within its budget whatever is cut.
    /// </summary>
    public static int ShownLength(IReadOnlyList<FileRead> files)
        => Lead.Length + files.Sum(f =>
        {
            var widest = Math.Max(f.TotalLines, f.Spans.Select(s => Math.Max(s.LastLine, s.FirstLine + s.Length)).DefaultIfEmpty(0).Max());
            var ranges = Enumerable.Repeat((widest, widest), Math.Max(1, 2 * f.Spans.Count)).ToArray();
            return 3 + Math.Max(f.Line([]).Length, f.Line(ranges).Length);
        });

    private string Line(IReadOnlyList<(int From, int To)> ranges)
    {
        var of = TotalKnown ? Invariant.N0(TotalLines) : "at least " + Invariant.N0(TotalLines);
        return ranges.Count == 0
            ? $"{Path}: no whole line of {of}"
            : ranges is [var one] && one.From == one.To ? $"{Path}: line {Invariant.N0(one.From)} of {of}"
            : $"{Path}: lines {string.Join(" and ", ranges.Select(r => r.From == r.To ? Invariant.N0(r.From) : $"{Invariant.N0(r.From)}-{Invariant.N0(r.To)}"))} of {of}";
    }

    /// <summary>The lines of this file shown whole in <paramref name="result"/>, of which only <paramref name="kept"/> is shown.</summary>
    internal IReadOnlyList<(int From, int To)> LinesShown(string result, IReadOnlyList<(int Start, int End)> kept)
    {
        var found = new List<(int From, int To)>();
        foreach (var s in Spans)
        {
            var start = Math.Clamp(s.Start, 0, result.Length);
            var declaredEnd = s.Start + s.Length;
            var end = Math.Clamp(declaredEnd, start, result.Length);

            // An empty stretch - an empty file - is shown whole where the result around it is.
            if (s.Length == 0)
            {
                if (s.StartsLine && s.EndsLine && kept.Any(k => k.Start <= start && start <= k.End))
                    found.Add((s.FirstLine, s.LastLine));
                continue;
            }

            foreach (var (keptStart, keptEnd) in kept)
            {
                var a = Math.Max(start, keptStart);
                var b = Math.Min(end, keptEnd);
                if (a >= b) continue;

                var lineAtA = s.FirstLine + Breaks(result, start, a);
                var lineAtB = lineAtA + Breaks(result, a, b);
                // A line counts only when all of it is shown: one the cut falls inside is not, and neither is the first of
                // a stretch that began part-way through it.
                var first = a == s.Start ? (s.StartsLine ? lineAtA : lineAtA + 1)
                    : result[a - 1] == '\n' ? lineAtA : lineAtA + 1;
                var last = b == declaredEnd ? (s.EndsLine ? s.LastLine : s.LastLine - 1)
                    : b < result.Length && result[b] == '\n' ? lineAtB : lineAtB - 1;
                if (first <= last) found.Add((first, last));
            }
        }

        var merged = new List<(int From, int To)>();
        foreach (var range in found.OrderBy(r => r.From))
            if (merged.Count > 0 && range.From <= merged[^1].To + 1)
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, range.To));
            else
                merged.Add(range);
        return merged;
    }

    private static int Breaks(string text, int from, int to)
    {
        var n = 0;
        for (var i = from; i < to; i++)
            if (text[i] == '\n') n++;
        return n;
    }
}
