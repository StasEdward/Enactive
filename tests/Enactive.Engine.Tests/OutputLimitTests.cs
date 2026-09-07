namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Three places where the size of the input decided how much memory this application used, and the
/// input was chosen by the agent or by whatever it ran.
///
/// <para>None of them was a leak. Each was an honest implementation that happened to hold everything
/// it was given: a command's whole stdout, a whole file to answer with twenty lines of it, and an
/// LCS matrix over every pair of lines in two versions of a document. All three now stop at a
/// ceiling and say that they did — a limit nobody is told about produces the worst of both, a
/// truncated answer that reads as a complete one.</para>
/// </summary>
public sealed class OutputLimitTests
{
    // ── a command's output ────────────────────────────────────────────────────────────

    [Fact]
    public void Captured_output_stops_at_the_ceiling_however_much_is_produced()
    {
        var stream = new ProcessExec.CapturedStream();

        // ~5 MB if it were all kept.
        for (var i = 0; i < 100_000; i++)
            stream.Add($"line {i} of a very talkative build");

        var text = stream.ToString();

        Assert.True(text.Length < 100_000, $"held {text.Length} characters");
        Assert.Contains("dropped", text, StringComparison.Ordinal);
    }

    // Bounded is not the same as lossy for ordinary output: a normal command still comes back whole
    // and unannotated.
    [Fact]
    public void Ordinary_output_is_kept_exactly_and_says_nothing_about_dropping()
    {
        var stream = new ProcessExec.CapturedStream();

        stream.Add("Build succeeded.");
        stream.Add("    0 Warning(s)");
        stream.Add("    0 Error(s)");

        var text = stream.ToString();

        Assert.Equal("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain("dropped", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_line_is_the_end_of_the_stream_not_a_line()
    {
        var stream = new ProcessExec.CapturedStream();

        stream.Add(null);
        stream.Add("only line");
        stream.Add(null);

        Assert.Equal("only line\n", stream.ToString().Replace("\r\n", "\n"));
    }

    // ── a diff the window would have to draw ──────────────────────────────────────────

    [Fact]
    public void An_ordinary_change_still_gets_a_line_by_line_diff()
    {
        var diff = TextDiff.Unified("one\ntwo\nthree\n", "one\nTWO\nthree\n", "doc.md");

        Assert.Contains("- two", diff, StringComparison.Ordinal);
        Assert.Contains("+ TWO", diff, StringComparison.Ordinal);
        Assert.Contains("  one", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_file_is_shown_as_added_lines()
    {
        var diff = TextDiff.Unified(null, "first\nsecond\n", "new.md");

        Assert.Contains("# new file: new.md", diff, StringComparison.Ordinal);
        Assert.Contains("+ first", diff, StringComparison.Ordinal);
    }

    // The one that mattered: an LCS matrix is one int per PAIR of lines, built on the UI thread while
    // a card is drawn. Two files of ten thousand lines is a hundred million cells — four hundred
    // megabytes and a frozen window, decided by a file the agent chose to write.
    [Fact]
    public void A_diff_too_large_to_read_is_not_attempted()
    {
        // Past the ceiling, but small enough that removing the ceiling merely produces a useless
        // diff rather than exhausting the test host - a differential check has to be survivable.
        var before = string.Join('\n', Enumerable.Range(0, 3_000).Select(i => $"line {i}"));
        var after = string.Join('\n', Enumerable.Range(0, 3_000).Select(i => $"line {i} changed"));

        var diff = TextDiff.Unified(before, after, "huge.md");

        Assert.Contains("too large to compare line by line", diff, StringComparison.Ordinal);
        Assert.Contains("3000 lines", diff, StringComparison.Ordinal);

        // It still says what it is and stays actionable.
        Assert.Contains("# modified: huge.md", diff, StringComparison.Ordinal);
        Assert.Contains("Apply and Reject still work", diff, StringComparison.Ordinal);
    }
}
