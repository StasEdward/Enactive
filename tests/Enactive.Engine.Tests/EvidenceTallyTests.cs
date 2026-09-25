namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 21:54. Step 2 made five edits to one file — four worked, one did
/// not match — ran the tests, and was rejected:
///
/// <para><c>"the RunEdgeCaseTests method was never actually added to the Program.cs file, as
/// indicated by the ERROR in the evidence. The agent's report is therefore based on a fabricated
/// scenario."</c></para>
///
/// <para>The method HAD been added, by the last of those edits, whose own output names the line it
/// went in at: <i>"Replaced 1 passage in 'tests/ParserSmokeTest/Program.cs' at line 264"</i>. One
/// ERROR was read as the state of the file, with four successes sitting beside it.</para>
///
/// <para>So the header counts them. A tally is much harder to misread than a list — "4 worked, 1
/// failed" cannot be reached by noticing one line — and it is the same reasoning that put the call
/// count there in the first place, after a shortened list was read as a short one.</para>
///
/// <para><b>What this does not claim.</b> The same run was also rejected for a second thing the
/// evidence plainly contradicted, and a count will not fix a reader that has already decided. This
/// makes the fact cheap to check; it cannot make it read.</para>
/// </summary>
public sealed class EvidenceTallyTests
{
    private static ExecutionJournal Journal(params (string Tool, ActionOutcome How, string Out)[] actions)
    {
        var journal = new ExecutionJournal();
        var n = 0;
        foreach (var (tool, how, output) in actions)
            journal.Record(1, tool, $$"""{"path":"file-{{n++}}.cs"}""", how, output);
        return journal;
    }

    /// <summary>The reported step, to shape: four edits that worked and one that did not.</summary>
    private static ExecutionJournal TheReportedStep()
        => Journal(
            ("edit_file", ActionOutcome.Succeeded, "Replaced 1 passage at line 9 (+62 bytes)."),
            ("edit_file", ActionOutcome.Succeeded, "Replaced 1 passage at line 232 (+3111 bytes)."),
            ("edit_file", ActionOutcome.Succeeded, "Replaced 1 passage at line 276 (+1408 bytes)."),
            ("edit_file", ActionOutcome.Failed, "'old_string' does not appear in Program.cs."),
            ("edit_file", ActionOutcome.Succeeded, "Replaced 1 passage at line 264 (-2159 bytes)."));

    // ── the count ───────────────────────────────────────────────────────────

    /// <summary>The one that matters: the successes are countable without counting them.</summary>
    [Fact]
    public void The_header_says_how_many_worked_and_how_many_did_not()
    {
        var evidence = TheReportedStep().Describe().Text;

        Assert.Contains("5 tool call(s) in this step, oldest first — 4 worked, 1 failed",
                        evidence, StringComparison.Ordinal);
    }

    /// <summary>Every outcome has a word, and they are named in a fixed order.</summary>
    [Fact]
    public void All_four_outcomes_are_counted()
    {
        var evidence = Journal(
            ("read_file", ActionOutcome.Succeeded, "contents"),
            ("read_file", ActionOutcome.Succeeded, "contents"),
            ("run_command", ActionOutcome.Failed, "error CS1002"),
            ("read_file", ActionOutcome.Answered, "offset 800 is past the end."),
            ("git", ActionOutcome.Refused, "blocked by the permission policy")).Describe().Text;

        Assert.Contains("5 tool call(s) in this step, oldest first — 2 worked, 1 failed, "
                        + "1 found nothing, 1 were refused", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing to compare when every call ended the same way — the count above already says it, and
    /// "3 worked" after "3 tool call(s)" is noise in a prompt that is paying for every character.
    /// </summary>
    [Theory]
    [InlineData(ActionOutcome.Succeeded)]
    [InlineData(ActionOutcome.Failed)]
    public void A_step_that_ended_one_way_says_it_once(ActionOutcome outcome)
    {
        var evidence = Journal(
            ("read_file", outcome, "x"), ("read_file", outcome, "y"), ("read_file", outcome, "z"))
            .Describe().Text;

        // The count, then straight on to the numbering — no tally between them. The comma is what
        // the header carries where a tally would have gone.
        Assert.Contains("3 tool call(s) in this step, oldest first, each numbered",
                        evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("worked", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tally counts the WHOLE step, including calls the budget could not show. That is the
    /// point: it is the one line that stays true when the list is cut.
    /// </summary>
    [Fact]
    public void The_tally_counts_calls_the_budget_could_not_show()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 199; i++)
            journal.Record(1, "read_file", $$"""{"path":"file-{{i}}-with-a-long-enough-name.cs"}""",
                           ActionOutcome.Succeeded, "contents");
        journal.Record(1, "run_command", """{"command":"dotnet build"}""", ActionOutcome.Failed, "error");

        var evidence = journal.Describe(maxChars: 1_500).Text;

        Assert.Contains("200 tool call(s)", evidence, StringComparison.Ordinal);
        Assert.Contains("199 worked, 1 failed", evidence, StringComparison.Ordinal);
        Assert.Contains("are not shown here", evidence, StringComparison.Ordinal);
    }

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>
    /// A retry's tally is the retry's. The mark is what makes a review judge the current attempt,
    /// and a count that reached back over it would be the first thing to give that away.
    /// </summary>
    [Fact]
    public void A_retry_counts_only_what_it_did()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "edit_file", """{"path":"a.cs"}""", ActionOutcome.Failed, "did not match");
        var mark = journal.Mark();
        journal.Record(1, "write_file", """{"path":"a.cs"}""", ActionOutcome.Succeeded, "Created");

        var evidence = journal.Describe(mark).Text;

        Assert.Contains("1 tool call(s)", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_that_ran_nothing_still_says_exactly_that()
        => Assert.Equal("(no tools were run in this step)", new ExecutionJournal().Describe().Text);

    /// <summary>The rest of the header survives — it is what stops a shortened result being read
    /// as a missing call.</summary>
    [Fact]
    public void The_shortening_notice_is_still_in_the_header()
        => Assert.Contains("the call it belongs to still happened",
                           TheReportedStep().Describe().Text, StringComparison.Ordinal);

    /// <summary>And the evidence still fits its budget with the tally in it.</summary>
    [Fact]
    public void The_evidence_still_fits_its_budget()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 40; i++)
            journal.Record(1, "read_file", $$"""{"path":"src/file-{{i}}.cs"}""",
                           ActionOutcome.Succeeded, new string('x', 9_000));
        journal.Record(1, "run_command", """{"command":"dotnet build"}""", ActionOutcome.Failed, "error");

        var evidence = journal.Describe(maxChars: 3_000).Text;

        Assert.True(evidence.Length <= 3_600, $"{evidence.Length} characters");
        Assert.Contains("40 worked, 1 failed", evidence, StringComparison.Ordinal);
    }
}
