namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Xunit;

/// <summary>
/// The content reviewer is shown the END of the file as well as the beginning.
///
/// <para><b>Measured on the run that FINISHED</b>, 2026-09-23, which is what makes it worth a test:
/// all four reviews passed and all four said, in their own notes, that they could not see the
/// work.</para>
///
/// <code>
/// [2] PASS: "Only the first 8000 characters were shown. They cover the pages 1-3 sections ..."
/// [3] PASS: "The excerpt covers only pages 1-3. The page 7-9 findin..."
/// [4] PASS: "The excerpt covers pages 1-2 ... and ends mid-line"
/// </code>
///
/// <para>Step 4 was reviewing pages 10-12. The report is one document each step APPENDS to, so the
/// new work is always at the end and a head-only excerpt showed the same opening four times. The
/// reviewer passed honestly — <c>ContentSystemPrompt</c> tells it to judge only what the excerpt
/// holds, and it obeyed. It was not wrong; it was shown the wrong 8000 characters.</para>
///
/// <para>This is §9q again — <i>"the verdict was at the end, and shortening kept the beginning"</i>
/// — fixed then for command output and never carried across to the file a reviewer reads. It is
/// the worst kind of defect this project collects: everything is green, and the green means
/// nothing.</para>
/// </summary>
public sealed class TheReviewerSeesTheWorkTests
{
    /// <summary>A document that grows by appending: the newest findings are the last lines.</summary>
    private static string Report(int pages)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Drift report");
        for (var page = 1; page <= pages; page++)
        {
            sb.AppendLine($"## Findings for page {page}");
            sb.AppendLine(new string('x', 900));
        }
        return sb.ToString();
    }

    private static string Shown(string content)
        => Reviewer.BuildContentUserPrompt(
            "Verify wiki pages 10-12",
            "Appended the findings for pages 10-12.",
            new[] { new WrittenFile("Docs/DRIFT.md", content, content.Length, false) });

    /// <summary>THE ONE THAT MATTERS. The step's own work is in the prompt.</summary>
    [Fact]
    public void The_newest_findings_reach_the_reviewer()
    {
        var content = Report(12);
        Assert.True(content.Length > 9000, "the file has to be past the per-file budget");

        var shown = Shown(content);

        Assert.Contains("Findings for page 12", shown, StringComparison.Ordinal);
        Assert.Contains("Findings for page 11", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the beginning stays: what a document IS lives in its opening, and a reviewer shown only
    /// the tail would judge findings with nothing to judge them against.
    /// </summary>
    [Fact]
    public void The_opening_is_still_there()
        => Assert.Contains("# Drift report", Shown(Report(12)), StringComparison.Ordinal);

    /// <summary>
    /// The cut says it is a cut, in the middle where it is. "NOT SHOWN" rather than "cut" is
    /// deliberate and older than this: on 2026-09-08 a reviewer read "1,645 characters cut" as
    /// evidence the file did not contain what was quoted, and failed the step twice for a value
    /// that was in them.
    /// </summary>
    [Fact]
    public void The_middle_says_it_is_missing()
        => Assert.Contains("characters not shown here; the end follows", Shown(Report(12)),
                            StringComparison.Ordinal);

    /// <summary>
    /// And the LABEL under it describes the slice that was taken. It went on saying "the first N
    /// characters" for a day after the slice became head-and-tail, and the reviewer believed the
    /// label over the text in front of it: on 2026-09-24 it passed step 4 with "the page 10-12
    /// sections this step reports appending are in the unseen part" - while those sections were in
    /// the prompt, at the bottom, where the tail had put them.
    ///
    /// <para>A wrong label is worse than the head-only cut it replaced. That one was at least
    /// honest about what it had left out.</para>
    /// </summary>
    [Fact]
    public void The_label_says_which_slice_it_is()
    {
        var shown = Shown(Report(12));

        Assert.Contains("START and the END", shown, StringComparison.Ordinal);
        Assert.Contains("missing is the MIDDLE", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("above is the first", shown, StringComparison.Ordinal);
    }

    /// <summary>A file that fits is untouched - no marker, no cut, nothing to explain.</summary>
    [Fact]
    public void A_short_file_is_shown_whole()
    {
        var shown = Shown(Report(2));

        Assert.Contains("Findings for page 1", shown, StringComparison.Ordinal);
        Assert.Contains("Findings for page 2", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("not shown here", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the instructions describe the shape the reviewer is actually given. They used to say an
    /// excerpt "stops where the excerpt stops" and to disregard "anything you expected to find
    /// further down" - true of a head-only cut and misleading about this one, which HAS a further
    /// down.
    /// </summary>
    [Fact]
    public void The_instructions_describe_the_excerpt_it_gets()
    {
        Assert.Contains("START", Reviewer.ContentSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("END of the file", Reviewer.ContentSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("further down", Reviewer.ContentSystemPrompt, StringComparison.Ordinal);
    }
}
