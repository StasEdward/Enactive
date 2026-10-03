namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Shortening output that does not fit, in the one way that keeps the answer.
///
/// <para>A program reports its OUTCOME last, so keeping the first N characters reliably hands over
/// the part with no answer in it. <c>ExecutionJournal</c> learned that on 2026-09-07 - a reviewer
/// accused an agent of fabricating a test summary that was real and one line past the cut - and
/// the rule stayed private to it, so the REVIEWER saw the end of a command's output and the MODEL
/// that ran the command did not.</para>
///
/// <para>On 2026-09-20 that cost a run: a step wrote its tests, ran them, could not tell whether
/// they had passed because the summary was past the cut, and spent its remaining turns trying to
/// pipe the output into a file until the stall guard stopped it.</para>
/// </summary>
public sealed class ShorteningTests
{
    private static string Noise(int lines)
        => string.Join('\n', Enumerable.Range(0, lines).Select(i => $"  restoring package {i} of many"));

    [Fact]
    public void Output_that_fits_is_untouched()
    {
        Assert.Equal("Build succeeded.", Shortening.ToFit("Build succeeded.", 1_000));
    }

    /// <summary>The property the whole change is for.</summary>
    [Fact]
    public void The_verdict_at_the_end_survives_the_cut()
    {
        var text = Noise(500) + "\nPassed!  - Failed: 0, Passed: 22";

        var shown = Shortening.ToFit(text, 2_000);

        Assert.Contains("Passed!  - Failed: 0, Passed: 22", shown, StringComparison.Ordinal);
        Assert.True(shown.Length <= 2_000 + 120, $"budget overrun: {shown.Length}");
    }

    /// <summary>And the start, so the reader can still tell what they are looking at.</summary>
    [Fact]
    public void The_start_survives_too()
    {
        var shown = Shortening.ToFit("FIRST LINE\n" + Noise(500) + "\nLAST LINE", 2_000);

        Assert.StartsWith("FIRST LINE", shown, StringComparison.Ordinal);
        Assert.EndsWith("LAST LINE", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice says NOT SHOWN rather than cut. A reviewer once read "1,645 characters cut from
    /// the middle" as evidence that a file did not contain what was quoted from it, and failed the
    /// step twice over a value inside those characters.
    /// </summary>
    [Fact]
    public void The_cut_announces_itself_without_reading_as_an_absence()
    {
        var shown = Shortening.ToFit(Noise(500), 2_000);

        Assert.Contains("not shown here", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("cut from", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Too small for two pieces and a marker between them: a clean cut, because half a marker is
    /// worse than none.
    /// </summary>
    [Fact]
    public void A_budget_with_no_room_for_a_marker_cuts_cleanly()
    {
        var shown = Shortening.ToFit(Noise(100), 50);

        Assert.DoesNotContain("not shown here", shown, StringComparison.Ordinal);
        Assert.Contains("of", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end, through the thing the model actually calls. The journal had this and the tool
    /// did not, which is the defect: the two audiences were shown different halves of the truth.
    /// </summary>
    /// <summary>
    /// Run 63e3cb, 2026-10-04: a command printed two lines and ran nothing - 115 characters against a budget of
    /// 100. It was shown to the review as a start, "(15 characters not shown here; the end follows)", and an end:
    /// the same shape as the 8,800-character result beside it, and longer than the text it stood for. The review
    /// read the short one as a long one cut, and passed a report that said the command had run 87 checks. A cut
    /// that hides no more than its own notice takes up saves nothing and says something false - that there was
    /// more to see.
    /// </summary>
    [Fact]
    public void A_result_barely_over_its_budget_is_shown_whole()
    {
        var text = "----- output -----\n" + new string('a', 80) + "\nNothing was run.";

        var shown = Shortening.HeadAndTail(text, text.Length - 15);

        Assert.Equal(text, shown);
    }

    [Fact]
    public void One_that_hides_more_than_its_notice_is_still_cut()
    {
        var text = new string('a', 400) + "\nverdict: 3 failed";

        var shown = Shortening.HeadAndTail(text, 200);

        Assert.Contains("characters not shown here", shown, StringComparison.Ordinal);
        Assert.True(shown.Length < text.Length);
        Assert.EndsWith("verdict: 3 failed", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_result_shows_the_model_the_end_as_well()
    {
        var log = Noise(4_000) + "\nBuild succeeded.\n    0 Warning(s)\n    0 Error(s)";

        var result = ProcessExec.BuildResult("Command", 0, log, "");

        Assert.Contains("0 Error(s)", result.Output, StringComparison.Ordinal);
        Assert.Contains("restoring package 0 of many", result.Output, StringComparison.Ordinal);
    }
}
