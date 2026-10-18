namespace Enactive.Core.Execution;

/// <summary>
/// What a write changed in a file, as a reviewer reads it: the lines it removed (<c>-</c>) and added (<c>+</c>), with a line
/// of unchanged text around them and <c>@@</c> giving where - bounded, and saying what it leaves out.
///
/// <para><b>Why.</b> The evidence showed a call by its arguments, compacted to their first 120 characters. An edit that
/// inserts a test the usual way - <c>old_string</c> an existing test (the anchor), <c>new_string</c> that test and the new
/// one after it - is then shown as the start of the ANCHOR. Run bbf67b, 2026-10-19, step 2: two tests were added that
/// way, the suite went from 43 to 45 passing and the report named them both; the reviewer, shown only the two anchors,
/// concluded the edits "replace code with different tests" than the report said, and failed the step. The right work
/// was put back and the run ended Failed. What an edit changed is the fact a reviewer needs, and the tool that made the
/// change is the one place that knows both sides of it.</para>
///
/// <para>Made by the tool (<see cref="MetadataKey"/>) from the text it read and the text it wrote, and carried by the
/// journal beside the call (<see cref="ExecutedAction.Change"/>); the call's arguments are recorded as they were. The
/// comparison is <see cref="LineDiff"/>'s - one diff in the engine - run only on what lies between the lines both sides
/// share at the start and the end, so a one-line edit of a long file costs a scan of it and not a table of its lines
/// squared.</para>
/// </summary>
/// <param name="Lines">The diff: <c>@@</c> headers and lines each starting <c>"+ "</c>, <c>"- "</c> or <c>"  "</c>.</param>
/// <param name="LinesNotKept">How many lines of the diff were not kept, past <see cref="MaxChars"/>.</param>
public sealed record WrittenChange(string Path, int Added, int Removed, IReadOnlyList<string> Lines, int LinesNotKept)
{
    /// <summary>The key a tool's result carries its change under.</summary>
    public const string MetadataKey = "writtenChange";

    /// <summary>
    /// The most of a diff kept per write. A whole-file rewrite would otherwise carry the whole file twice, into the
    /// journal and the log, for a reviewer whose evidence block is 6,000 characters for every call of the step.
    /// </summary>
    public const int MaxChars = 2_000;

    /// <summary>Unchanged lines kept on each side of a change: enough to say where it is, not so many that they are it.</summary>
    private const int Context = 1;

    /// <summary>A line longer than this is clipped: a minified file is one line, and one line must not be the whole diff.</summary>
    private const int MaxLineChars = 200;

    /// <summary>
    /// The change from <paramref name="before"/> to <paramref name="after"/>; null when there was no file before (a new
    /// file is all additions, and its arguments already show its start) or nothing changed.
    /// </summary>
    public static WrittenChange? Of(string? before, string after, string path, int maxChars = MaxChars)
    {
        if (before is null) return null;
        var old = Split(before);
        var now = Split(after);

        // The lines both share at the start and at the end are not compared - they are the same, and comparing them is
        // what makes a diff of a long file cost its length squared (LineDiff refuses past a limit).
        var same = 0;
        while (same < old.Length && same < now.Length && old[same] == now[same]) same++;
        var sameEnd = 0;
        while (sameEnd < old.Length - same && sameEnd < now.Length - same && old[^(sameEnd + 1)] == now[^(sameEnd + 1)]) sameEnd++;

        var removedLines = old[same..(old.Length - sameEnd)];
        var addedLines = now[same..(now.Length - sameEnd)];
        if (removedLines.Length == 0 && addedLines.Length == 0) return null;

        if (LineDiff.Lines(removedLines, addedLines) is not { } diff)
            return new WrittenChange(path, addedLines.Length, removedLines.Length,
                [$"@@ from line {same + 1}: {Invariant.N0(removedLines.Length)} line(s) replaced by {Invariant.N0(addedLines.Length)} - too many to compare line by line @@"], 0);

        var lead = Math.Min(Context, same);
        var ops = new List<(char Op, string Line)>(diff.Count + 2 * Context);
        for (var k = same - lead; k < same; k++) ops.Add((' ', old[k]));
        ops.AddRange(diff);
        for (var k = old.Length - sameEnd; k < Math.Min(old.Length, old.Length - sameEnd + Context); k++) ops.Add((' ', old[k]));

        var lines = Hunks(ops, same - lead + 1);
        var kept = new List<string>();
        var used = 0;
        foreach (var line in lines)
        {
            if (used + line.Length + 1 > maxChars) break;
            kept.Add(line);
            used += line.Length + 1;
        }
        return new WrittenChange(path, diff.Count(d => d.Op == '+'), diff.Count(d => d.Op == '-'), kept, lines.Count - kept.Count);
    }

    /// <summary>One line, for the call it belongs to: which file, and how much changed in it.</summary>
    public string Summary => $"{Path}: {Invariant.N0(Added)} line(s) added, {Invariant.N0(Removed)} removed";

    /// <summary>
    /// The diff in at most <paramref name="room"/> characters, its note included. What goes first when it does not fit
    /// is the unchanged lines around a change, then changed lines from the end - and how many are not shown is said, so a
    /// shortened diff does not read as a smaller change.
    /// </summary>
    public string Body(int room = int.MaxValue)
    {
        var whole = string.Join("\n", Lines) + (LinesNotKept > 0 ? "\n" + NotShown(LinesNotKept) : "");
        if (whole.Length <= room) return whole;

        var changed = Lines.Where(l => !l.StartsWith("  ", StringComparison.Ordinal)).ToList();
        var dropped = Lines.Count - changed.Count + LinesNotKept;
        var shown = new List<string>();
        var used = 0;
        foreach (var line in changed)
        {
            var hiddenAfter = dropped + changed.Count - shown.Count - 1;
            var note = hiddenAfter > 0 ? NotShown(hiddenAfter).Length + 1 : 0;
            if (used + line.Length + 1 + note > room) break;
            shown.Add(line);
            used += line.Length + 1;
        }
        var hidden = dropped + changed.Count - shown.Count;
        if (hidden > 0)
        {
            var note = NotShown(hidden);
            if (used + note.Length > room) return shown.Count == 0 ? "" : string.Join("\n", shown);
            shown.Add(note);
        }
        return string.Join("\n", shown);
    }

    /// <summary>The whole of it, as the tool log prints a result's metadata.</summary>
    public override string ToString() => Summary + "\n" + Body();

    private static string NotShown(int lines)
        => $"… {Invariant.N0(lines)} line(s) of this diff not shown here";

    /// <summary>The changed lines with <see cref="Context"/> lines around each, under a header saying where they are.</summary>
    private static List<string> Hunks(List<(char Op, string Line)> ops, int firstLine)
    {
        // The old and the new line number at each position.
        var oldAt = new int[ops.Count + 1];
        var newAt = new int[ops.Count + 1];
        oldAt[0] = newAt[0] = firstLine;
        for (var i = 0; i < ops.Count; i++)
        {
            oldAt[i + 1] = oldAt[i] + (ops[i].Op == '+' ? 0 : 1);
            newAt[i + 1] = newAt[i] + (ops[i].Op == '-' ? 0 : 1);
        }

        var changed = Enumerable.Range(0, ops.Count).Where(i => ops[i].Op != ' ').ToArray();
        var lines = new List<string>();
        for (var g = 0; g < changed.Length;)
        {
            // Changes close enough that the context between them would be shown anyway are one hunk.
            var last = g;
            while (last + 1 < changed.Length && changed[last + 1] - changed[last] <= 2 * Context + 1) last++;
            var from = Math.Max(0, changed[g] - Context);
            var to = Math.Min(ops.Count - 1, changed[last] + Context);
            var oldCount = Enumerable.Range(from, to - from + 1).Count(i => ops[i].Op != '+');
            var newCount = Enumerable.Range(from, to - from + 1).Count(i => ops[i].Op != '-');
            lines.Add($"@@ -{oldAt[from]},{oldCount} +{newAt[from]},{newCount} @@");
            for (var i = from; i <= to; i++)
                lines.Add($"{ops[i].Op} {Clip(ops[i].Line)}");
            g = last + 1;
        }
        return lines;
    }

    private static string Clip(string line)
        => line.Length <= MaxLineChars ? line : line[..MaxLineChars] + $"… ({Invariant.N0(line.Length)} characters in the line)";

    private static string[] Split(string text) => text.Replace("\r\n", "\n").Split('\n');
}
