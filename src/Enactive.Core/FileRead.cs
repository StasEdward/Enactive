namespace Enactive.Core.Execution;

using System.Text;

/// <summary>
/// What a read call's result holds of one file it was asked for: which file, how many lines it has, and where in the result
/// its text is - so the step reviewer, shown that result cut to its share of the evidence, is told the file was read and
/// which of its lines it is shown. A file asked for and not read says why instead (<see cref="NotRead"/>).
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
/// <param name="NotRead">Why a file the call asked for was not read - "not there", or past what one call may return - and
/// null for a file read. Said because the tool's own line saying so sits in the middle of its result, where a cut to fit
/// takes it out (code review of task 21).</param>
public sealed record FileRead(string Path, int TotalLines, bool TotalKnown, IReadOnlyList<FileRead.Span> Spans, string? NotRead = null)
{
    /// <summary>The key a tool's result carries the files it was asked for under: an <c>IReadOnlyList&lt;FileRead&gt;</c>.</summary>
    public const string MetadataKey = "filesRead";

    /// <summary>
    /// The most of a call line spent naming the files a call asked for. Generous on purpose - read_files takes at most ten
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

    /// <summary>Whether any file of <paramref name="files"/> was read - what a read is shown by its files for.</summary>
    public static bool AnyRead(IReadOnlyList<FileRead> files) => files.Any(f => f.NotRead is null);

    /// <summary>
    /// One line, for the call it belongs to: the file it read, or how many and each by name, then those asked for and not
    /// read, each with why. Given what of <paramref name="result"/> is shown (<paramref name="kept"/>, as [start, end) - its
    /// start and its end when it is cut), each file read says beside its name which of its lines are: "a.cs (lines 1-40 of
    /// 264 shown)". Beside the name and not in a list of its own under the result: such a list named every file a second
    /// time, and doubled what the least of a read costs, so a step of many reads lost more of them from its evidence
    /// whole, names and all (code review of task 21).
    /// </summary>
    public static string Summary(IReadOnlyList<FileRead> files, string? result = null, IReadOnlyList<(int Start, int End)>? kept = null)
    {
        var read = files.Where(f => f.NotRead is null).ToArray();
        var sb = new StringBuilder(read.Length == 1 ? "" : $"{Invariant.N0(read.Length)} files: ");
        // Which are named is decided on the names alone, so a call names the same files whatever is said beside them.
        var plain = sb.Length;
        var named = 0;
        foreach (var (file, k) in read.Select((f, k) => (f, k)).Concat(files.Where(f => f.NotRead is not null).Select((f, k) => (f, -1 - k))))
        {
            var part = (k == 0 ? "" : k == -1 ? (read.Length > 0 ? "; not read: " : "not read: ") : ", ") + file.Path
                       + (file.NotRead is { } why ? $" ({why})" : "");
            // The first is named whatever its length: a call line that names no file is the defect this is here to end.
            if (named > 0 && plain + part.Length > MaxPathsChars)
                return sb.Append($", and {Invariant.N0(files.Count - named)} more not named here").ToString();
            sb.Append(part);
            plain += part.Length;
            if (file.NotRead is null && result is not null && kept is not null)
                sb.Append(file.Beside(file.LinesShown(result, kept)));
            named++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The most that what <see cref="Summary"/> says beside each file can add to it, for a result kept as at most two
    /// stretches - its start and its end: the room a caller keeps, so the evidence stays within its budget whatever is cut.
    /// Two ordered stretches meet a file's stretches in at most one more piece than the file has stretches, and no line
    /// number is past the last line a stretch holds (<see cref="Span.LastLine"/>) or the file's count; every piece is then
    /// written at its widest, as "from-to".
    /// </summary>
    public static int ShownLength(IReadOnlyList<FileRead> files)
        => files.Where(f => f.NotRead is null).Sum(f =>
        {
            var widest = Invariant.N0(Math.Max(f.TotalLines, f.Spans.Select(s => s.LastLine).DefaultIfEmpty(0).Max()));
            var pieces = Enumerable.Repeat($"{widest}-{widest}", f.Spans.Count + 1);
            return Math.Max(f.Beside([]).Length, $" (lines {string.Join(" and ", pieces)} of {f.Of} shown)".Length);
        });

    private string Of => TotalKnown ? Invariant.N0(TotalLines) : "at least " + Invariant.N0(TotalLines);

    private string Beside(IReadOnlyList<(int From, int To)> ranges)
        // "Here", and "read": what a file with none shown is, is a file read and not shown in this evidence - the
        // difference the reviewer of run 2eedd5 was never told.
        => ranges.Count == 0 ? $" (read; none of its {Of} lines shown here)"
            : ranges is [var one] && one.From == one.To ? $" (line {Invariant.N0(one.From)} of {Of} shown)"
            : $" (lines {string.Join(" and ", ranges.Select(r => r.From == r.To ? Invariant.N0(r.From) : $"{Invariant.N0(r.From)}-{Invariant.N0(r.To)}"))} of {Of} shown)";

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
