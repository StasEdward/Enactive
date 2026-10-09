namespace Enactive.Agents;

using System.Text;

/// <summary>
/// Watches the TEXT of a reply while it streams, and says when it has run away: the same passage
/// coming round again and again, or far more prose than any turn of a tool loop needs.
///
/// <para><b>Measured 2026-09-24 19:51, run 99f7dc1d.</b> One reply streamed plain text for more than
/// six minutes - 15,000 tokens and counting, with 40,000 allowed because max_tokens is what is left of
/// the window. No tool call, no reasoning: nothing in the engine watched it and nothing reached the
/// log. The reply before it had already "confirmed" five facts in a row as prose, citing files and
/// line numbers, before making one call. A model that narrates checks instead of making them, or that
/// has fallen into a loop, looks the same from outside, on any provider: a long silence that ends in
/// an answer nobody can use.</para>
///
/// <para>Only the text is watched. Tool arguments are a file being written, and a file may be long
/// and may legitimately repeat itself (a table, test data); prose in a tool loop is narration, and
/// narration has no reason to do either.</para>
/// </summary>
internal sealed class RunawayReply
{
    /// <summary>
    /// The most prose one reply may carry. About 10,000 tokens: several times any step summary, and a
    /// quarter of what the window let the measured reply run to. A long RESULT belongs in a file,
    /// which is what the stop tells the model.
    /// </summary>
    internal const int MaxTextChars = 40_000;

    /// <summary>How often the repetition check runs; it looks at the tail, so it need not run per token.</summary>
    internal const int CheckEveryChars = 1_000;

    /// <summary>A passage repeated this many times in a row, back to back, is a loop.</summary>
    internal const int LoopRepeats = 3;

    /// <summary>
    /// The shortest passage counted as a loop. Shorter repeats are how text is shaped - table rows,
    /// separators, list markers - not a model going round.
    /// </summary>
    internal const int ShortestLoopChars = 40;

    /// <summary>The longest passage looked for: a paragraph or a few. Longer loops are caught by the size.</summary>
    internal const int LongestLoopChars = 4_000;

    /// <summary>
    /// A passage must have at least this many different characters to count: a separator line of 120
    /// dashes repeats with every period, and is not a loop.
    /// </summary>
    private const int DistinctCharsInALoop = 10;

    private int _checkedAt;

    /// <summary>Why a reply was stopped, and how much of its text to keep - and whether what went round was its reasoning.</summary>
    internal sealed record Stop(string Reason, int KeepChars, bool Looped, bool InReasoning = false);

    /// <summary>
    /// Called as REASONING arrives: a loop only - its size is the provider's reasoning allowance to bound, not this. Run
    /// f08f1e, 2026-10-09: three turns of nothing but reasoning, 50-59 thousand characters each and no call, every one cut
    /// at the 16,384-token limit after about four minutes - and each had begun repeating one paragraph word for word by its
    /// 4,000th to 9,000th character ("in the next test I call MakeMove(0), MakeMove(3), MakeMove(6) again ..."). Watched,
    /// each would have stopped at a few thousand tokens; twelve minutes went on the rest. The day before, a reasoning that
    /// circled without repeating itself (run bb77e810) was measured too: that kind is not caught, and is not meant to be.
    /// </summary>
    public Stop? LoopIn(StringBuilder reasoning)
    {
        if (reasoning.Length - _checkedAt < CheckEveryChars)
            return null;
        _checkedAt = reasoning.Length;
        return Loop(reasoning.ToString()) is { } loop ? loop with { InReasoning = true } : null;
    }

    /// <summary>Called as text arrives. Null while the reply is fine.</summary>
    public Stop? After(StringBuilder text)
    {
        if (text.Length - _checkedAt >= CheckEveryChars)
        {
            _checkedAt = text.Length;
            if (Loop(text.ToString()) is { } loop)
                return loop;
        }

        if (text.Length >= MaxTextChars)
            return new Stop(
                $"it had written {text.Length:N0} characters of text without a tool call, and a turn is "
                + $"allowed {MaxTextChars:N0}", text.Length, Looped: false);

        return null;
    }

    private static Stop? Loop(string text)
    {
        var longest = Math.Min(LongestLoopChars, text.Length / LoopRepeats);
        var tail = text.AsSpan();

        for (var period = ShortestLoopChars; period <= longest; period++)
        {
            var span = tail[^(period * LoopRepeats)..];
            var passage = span[..period];

            var repeats = true;
            for (var k = 1; k < LoopRepeats && repeats; k++)
                repeats = span.Slice(k * period, period).SequenceEqual(passage);
            if (!repeats || Distinct(passage) < DistinctCharsInALoop)
                continue;

            // Where the loop began: walk back while the text still agrees with itself one period on.
            var start = text.Length - period * LoopRepeats;
            while (start > 0 && text[start - 1] == text[start - 1 + period])
                start--;

            // Where a loop "begins" is only known to within a period - the text before it may end the
            // way each pass ends - so the first pass is kept to the end of the line it stops in,
            // rather than cut in the middle of a word.
            var keep = start + period;
            var lineEnd = text.IndexOf('\n', keep);
            if (lineEnd >= 0 && lineEnd < start + 2 * period)
                keep = lineEnd + 1;

            var times = (text.Length - start) / period;
            return new Stop(
                $"the same passage of {period:N0} characters came {times} times in a row",
                KeepChars: keep, Looped: true);
        }

        return null;
    }

    /// <summary>The last non-empty line of the text so far, quoted and shortened - what a progress
    /// line shows of a reply that is still being written.</summary>
    internal static string LastLine(StringBuilder text)
    {
        const int Shown = 120;
        var from = Math.Max(0, text.Length - 2_000);
        var tail = text.ToString(from, text.Length - from).TrimEnd();
        var line = tail[(tail.LastIndexOf('\n') + 1)..].Trim();
        if (line.Length > Shown)
            line = line[..Shown] + "…";
        return line.Length == 0 ? "" : $"\"{line}\"";
    }

    private static int Distinct(ReadOnlySpan<char> passage)
    {
        var seen = new HashSet<char>();
        foreach (var c in passage)
            if (seen.Add(c) && seen.Count >= DistinctCharsInALoop)
                break;
        return seen.Count;
    }
}
