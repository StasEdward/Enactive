namespace Enactive.Core.Execution;

/// <summary>
/// Shortening a piece of output that does not fit, in the one way that keeps the answer.
///
/// <para><b>Why the middle goes and not the end.</b> A program reports its OUTCOME last. Restore
/// chatter, node numbers, per-file progress and certificate notices come first; "Build succeeded",
/// "Passed! - Failed: 0, Passed: 22", "error CS0246 ... 4 Error(s)" come last. Keeping the first N
/// characters is the obvious implementation and it reliably hands over the part with no answer in
/// it.</para>
///
/// <para><b>This is not a rule about builds.</b> It is a rule about commands, because reporting
/// the outcome at the end is what programs do - dotnet, npm, docker, pytest, git, msbuild alike.
/// Where the output has no outcome at all, a directory listing or a dumped file, neither end is
/// special and showing both is no worse than showing one.</para>
///
/// <para><b>What it costs.</b> The head shrinks: the same budget that was all head becomes two
/// fifths head and three fifths tail. Something that sat between those fifths and was previously
/// shown is now not. That is a real loss and it is narrow - a runtime puts its exception message
/// first, and MSBuild repeats its errors in the summary at the end - and it is the trade this
/// exists to make: a smaller chance of losing the middle against a near-certainty of losing the
/// verdict.</para>
///
/// <para><b>Written once, here.</b> It lived as a private method inside <c>ExecutionJournal</c>,
/// so the REVIEWER was shown the end of a command's output and the MODEL that ran the command was
/// not. Same failure, two audiences, fixed for one of them - which is the shape of defect this
/// repository keeps finding.</para>
/// </summary>
public static class Shortening
{
    /// <summary>
    /// Below this there is no room for two pieces and a marker between them, and half a marker is
    /// worse than a clean cut.
    /// </summary>
    private const int SmallestTail = 40;

    /// <summary>
    /// The share of the budget spent on the START. Two fifths for a command, because a program
    /// reports its outcome last and the end is where the answer is.
    /// </summary>
    public const double CommandHead = 0.4;

    /// <summary>
    /// And three fifths for a FILE, because the reasoning above is about commands and does not
    /// carry: a source file's top holds its namespace, its usings and its declaration, and the
    /// bottom holds its last member and a row of closing braces. The end is still worth keeping -
    /// a cut that hides the end hides that there IS one - but it is not where the answer lives.
    /// </summary>
    public const double FileHead = 0.6;

    /// <summary>
    /// What a command's result puts between what the program printed and what it wrote to its error stream
    /// (ProcessExec). Every cut here keeps each side's own end, because each side ends with something: the program's
    /// outcome is the last line of its output ("Failed! - Failed: 6, Passed: 94"), and its last error is the last line
    /// of the other. Cut as one text, the error stream was all the end there was - run 9c0ee4, 2026-10-08: a reviewer
    /// shown a test run that ended in six "[FAIL]" lines of stderr, with the totals line cut out of the middle above
    /// them, failed a step for reporting totals "not evidenced by any visible call output". The step had read them.
    /// </summary>
    public const string StderrSection = "\n[stderr]\n";

    /// <summary><paramref name="text"/> unchanged when it fits, else its start and its end.</summary>
    public static string ToFit(string text, int budget, double headShare = CommandHead)
        => text.Length <= budget ? text : HeadAndTail(text, budget, headShare);

    /// <summary>
    /// The END of <paramref name="text"/>, about <paramref name="chars"/> of it, said to be the end - for a reader who
    /// is given the outcome and nothing else (a handover's last command, a check that fails before the work). A
    /// command's two streams each keep their end (<see cref="StderrSection"/>).
    /// </summary>
    public static string End(string text, int chars)
        => text.Length <= chars ? text
            : Streams(text) is { } streams ? EachStream(streams, chars, OneEnd)
            : OneEnd(text, chars);

    /// <summary>
    /// The start and the end, with the cut marked between them.
    ///
    /// <para>"NOT SHOWN", not "cut". On 2026-09-08 17:31 a reviewer read "1,645 characters cut from
    /// the middle" as evidence that the file did not contain what the agent had quoted from it, and
    /// failed the step twice for a value that was in those 1,645 characters. What the notice itself
    /// can do is stop reading as an absence; the rest of that rule belongs in the instructions of
    /// whoever is reading, where it is said once.</para>
    /// </summary>
    /// <param name="headShare">
    /// How much of the budget goes to the START — <see cref="CommandHead"/> or
    /// <see cref="FileHead"/>. A parameter rather than two functions, because the SHAPE of the cut
    /// has to stay one shape: a reader who learns "an excerpt is the start and the end" and then
    /// meets one place where it is not will misread it. That is not a hypothetical — it happened
    /// on 2026-09-24, when a label describing the old shape survived the new one and a reviewer
    /// believed the label over the text in front of it (§9cc).
    /// </param>
    public static string HeadAndTail(string text, int budget, double headShare = CommandHead)
        => text.Length <= budget ? text
            : Streams(text) is { } streams ? EachStream(streams, budget, (part, room) => OneHeadAndTail(part, room, headShare))
            : OneHeadAndTail(text, budget, headShare);

    /// <summary>A command's output and its error stream, when the text is one with both (<see cref="StderrSection"/>).</summary>
    private static (string Output, string Errors)? Streams(string text)
        => text.IndexOf(StderrSection, StringComparison.Ordinal) is >= 0 and var at
            ? (text[..at], text[(at + StderrSection.Length)..])
            : null;

    /// <summary>
    /// Each stream cut to its share of the room, the section between them kept. A stream that fits in half the room is
    /// kept whole and the other is given the rest; when neither does, each gets half - so neither end is ever the other
    /// stream's to give away.
    /// </summary>
    private static string EachStream((string Output, string Errors) streams, int budget, Func<string, int, string> cut)
    {
        var room = Math.Max(0, budget - StderrSection.Length);
        var forOutput = streams.Output.Length <= room / 2 ? streams.Output.Length
            : streams.Errors.Length <= room / 2 ? room - streams.Errors.Length
            : room / 2;
        return cut(streams.Output, forOutput) + StderrSection + cut(streams.Errors, room - forOutput);
    }

    private static string OneEnd(string text, int chars)
        => text.Length <= chars ? text
            : $"({text.Length - chars} characters of the start not shown; the end follows) " + text[^chars..];

    private static string OneHeadAndTail(string text, int budget, double headShare)
    {
        if (text.Length <= budget)
            return text;

        // Enough head to recognise what this IS; the rest to the end, which for a command is where
        // the answer is and for a file is at least proof that there was an end.
        var head = Math.Max(1, (int)(budget * Math.Clamp(headShare, 0.1, 0.9)));
        var tail = budget - head;

        if (tail < SmallestTail)
            return text[..budget] + $"… ({budget:N0} of {text.Length:N0})";

        var notice = $"\n… ({text.Length - budget:N0} characters not shown here; the end follows) …\n";

        // A cut that hides no more than its own notice takes up is not made: it would be longer than
        // the text it stands for, and it would say there was more to see. On 2026-10-04 a command
        // that printed two lines and ran nothing reached a review as a start, "(15 characters not
        // shown here…)" and an end - the shape of the long result beside it - and the review took it
        // for a long result cut, and passed a report that said the command had done the work.
        if (text.Length - budget <= notice.Length)
            return text;

        return text[..head] + notice + text[^tail..];
    }
}
