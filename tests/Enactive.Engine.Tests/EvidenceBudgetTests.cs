namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 19:05: a documentation-sync run failed with its second step
/// skipped, and the reviewer's reason was
///
/// <para><c>"the tool execution evidence shows it only listed the root directory and read the
/// README.md. No source files in the src/ directory were ever read."</c></para>
///
/// <para>Five source files had been read. The log shows every one of them. What the reviewer was
/// shown ended <c>… (truncated)</c> in the middle of the README, because the evidence was assembled
/// whole and then cut at 3000 characters — so one long result at the START ate the budget and every
/// call after it disappeared from the evidence, though not from the run.</para>
///
/// <para>The reviewer then did exactly what it is instructed to do and failed work that had been
/// done. Three times. Then the second step was skipped and the run died.</para>
///
/// <para>This is §8i again in the one place built to prevent it. The journal fixed WHERE the
/// evidence comes from — a record nothing that shortens a prompt can touch — and left the same hole
/// in how it is rendered: a record shortened at the front, judged as though it were whole.</para>
///
/// <para>The rule now: <b>every call is listed; only outputs are shortened.</b> A call line is a
/// dozen characters and is what proves the call happened; a result is bulky and is supporting
/// detail. That is the same principle as <c>Transcript.Elide</c> — elide content, never remove the
/// record that something happened.</para>
/// </summary>
public sealed class EvidenceBudgetTests
{
    private static ExecutionJournal Journal(params (string Tool, string Args, string Output)[] actions)
    {
        var journal = new ExecutionJournal();
        foreach (var (tool, args, output) in actions)
            journal.Record(1, tool, args, ActionOutcome.Succeeded, output);
        return journal;
    }

    /// <summary>The step from the log, to scale: a directory, a long README, then five source files.</summary>
    private static ExecutionJournal TheReportedStep()
        => Journal(
            ("list_dir", """{"path":"."}""", ".continue/ .enactive/ .git/ README.md src/ tests/"),
            ("read_file", """{"path":"README.md"}""", new string('R', 12_000)),
            ("read_file", """{"path":"src/ArcMapConditions.App/ViewModels/MainViewModel.cs"}""", new string('M', 9_000)),
            ("read_file", """{"path":"src/ArcMapConditions.App/Services/StartupManager.cs"}""", new string('S', 4_000)),
            ("read_file", """{"path":"src/ArcMapConditions.App/Core/ConditionsParser.cs"}""", new string('C', 8_000)),
            ("read_file", """{"path":"src/ArcMapConditions.App/Services/MapConditionsService.cs"}""", new string('P', 7_000)),
            ("read_file", """{"path":"src/ArcMapConditions.App/Services/NotificationService.cs"}""", new string('N', 5_000)));

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>
    /// The one that matters. Every file the step read is named in the evidence, however long the
    /// first result was.
    /// </summary>
    [Fact]
    public void Every_call_is_listed_however_long_the_first_result_was()
    {
        var evidence = TheReportedStep().Describe();

        foreach (var file in new[]
                 {
                     "README.md", "MainViewModel.cs", "StartupManager.cs",
                     "ConditionsParser.cs", "MapConditionsService.cs", "NotificationService.cs"
                 })
            Assert.Contains(file, evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the evidence stays inside its budget while doing it — the fix is a fair split, not a
    /// bigger number. A bigger number only moves the wall.
    /// </summary>
    [Fact]
    public void The_evidence_still_fits_its_budget()
    {
        var evidence = TheReportedStep().Describe(maxChars: 3_000);

        Assert.True(evidence.Length <= 3_600, $"{evidence.Length} characters");
        Assert.Contains("NotificationService.cs", evidence, StringComparison.Ordinal);
        Assert.Contains("MainViewModel.cs", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count is stated. A reviewer that can compare "7 tool call(s)" against what it can see
    /// can tell a short list from a shortened one — which is the distinction it got wrong.
    /// </summary>
    [Fact]
    public void The_number_of_calls_is_a_fact_not_something_to_count()
        => Assert.Contains("7 tool call(s)", TheReportedStep().Describe(), StringComparison.Ordinal);

    /// <summary>
    /// A shortened result says so WHERE IT IS, and says the call happened. Both halves matter: the
    /// reviewer's mistake was reading absence of output as absence of action.
    /// </summary>
    [Fact]
    public void A_shortened_result_says_so_and_says_the_call_happened()
    {
        var evidence = Journal(("read_file", """{"path":"big.cs"}""", new string('x', 50_000))).Describe();

        // The mark moved into the middle when shortening started keeping the END too — see
        // ResultTailTests. What it has to do is unchanged: say a gap happened, and how big it was.
        Assert.Contains("characters not shown here", evidence, StringComparison.Ordinal);
        // ...and what that mark MEANS is said once, at the top, rather than eighty-five characters
        // at a time next to every result it annotates.
        Assert.Contains("the call it belongs to still happened", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The block obeys a budget the CALLER set, because how much evidence is enough is a property of
    /// the work and not of the engine.
    ///
    /// <para>Reported 2026-09-08 17:31. An analysis step made thirteen calls; the default 6,000
    /// shared between them left about 320 characters of each output, and the step was failed twice
    /// for quoting a value that had been cut out of one of them. 320 characters of a source file
    /// cannot support or refute anything quoted from it, and no fixed number is right for both that
    /// step and one that runs a single command.</para>
    /// </summary>
    [Theory]
    [InlineData(2_000)]
    [InlineData(6_000)]
    [InlineData(20_000)]
    public void The_budget_the_caller_asks_for_is_the_budget_it_gets(int budget)
    {
        var journal = Journal(
            ("read_file", """{"path":"a.cs"}""", new string('a', 40_000)),
            ("read_file", """{"path":"b.cs"}""", new string('b', 40_000)),
            ("read_file", """{"path":"c.cs"}""", new string('c', 40_000)));

        Assert.True(journal.Describe(maxChars: budget).Length <= budget,
                    $"{journal.Describe(maxChars: budget).Length} characters against a budget of {budget}");
    }

    /// <summary>
    /// A bigger budget buys MORE of each result, not more results. The list is never what gets
    /// traded away — that is §8i, and it is why the budget is shared rather than spent first-come.
    /// </summary>
    [Fact]
    public void A_bigger_budget_shows_more_of_each_result()
    {
        var journal = Journal(
            ("read_file", """{"path":"a.cs"}""", new string('a', 40_000)),
            ("read_file", """{"path":"b.cs"}""", new string('b', 40_000)));

        var small = journal.Describe(maxChars: 3_000);
        var large = journal.Describe(maxChars: 20_000);

        Assert.True(large.Length > small.Length * 3);
        foreach (var evidence in new[] { small, large })
        {
            Assert.Contains("a.cs", evidence, StringComparison.Ordinal);
            Assert.Contains("b.cs", evidence, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One enormous result must not starve the others. This is the whole failure in one assertion:
    /// first-come budgeting gave everything to the README.
    /// </summary>
    [Fact]
    public void One_enormous_result_does_not_starve_the_rest()
    {
        var evidence = Journal(
            ("read_file", """{"path":"huge.md"}""", new string('H', 100_000)),
            ("read_file", """{"path":"small.cs"}""", "namespace Small; // the whole file"),
            ("run_command", """{"command":"dotnet build"}""", "Build succeeded. 0 Error(s)")).Describe();

        Assert.Contains("small.cs", evidence, StringComparison.Ordinal);
        Assert.Contains("namespace Small", evidence, StringComparison.Ordinal);
        Assert.Contains("Build succeeded", evidence, StringComparison.Ordinal);
    }

    // ── what must not change ────────────────────────────────────────────────

    [Fact]
    public void A_step_that_ran_nothing_still_says_exactly_that()
        => Assert.Equal("(no tools were run in this step)", new ExecutionJournal().Describe());

    /// <summary>A refusal is evidence too, and is never mistaken for a success.</summary>
    [Fact]
    public void A_refusal_is_still_recorded_as_one()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "git", """{"args":["push"]}""", ActionOutcome.Refused, "blocked by the permission policy");

        var evidence = journal.Describe();

        Assert.Contains("REFUSED", evidence, StringComparison.Ordinal);
        Assert.Contains("blocked by the permission policy", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_is_still_recorded_as_one()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", """{"command":"dotnet build"}""", ActionOutcome.Failed, "error CS1002");

        Assert.Contains("ERROR: error CS1002", journal.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A retry judges the CURRENT attempt: the mark still skips what a rejected attempt did, or
    /// every retry would be reviewed against the run it was meant to replace.
    /// </summary>
    [Fact]
    public void A_retry_still_starts_from_the_mark()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "read_file", """{"path":"first-attempt.cs"}""", ActionOutcome.Succeeded, "old");
        var mark = journal.Mark();
        journal.Record(1, "read_file", """{"path":"second-attempt.cs"}""", ActionOutcome.Succeeded, "new");

        var evidence = journal.Describe(mark);

        Assert.Contains("second-attempt.cs", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("first-attempt.cs", evidence, StringComparison.Ordinal);
        Assert.Contains("1 tool call(s)", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// write_file carries a whole file as its ARGUMENTS. Clipped, and said - otherwise one write
    /// costs the same budget the outputs were rescued from.
    /// </summary>
    [Fact]
    public void A_call_whose_arguments_are_a_whole_file_is_clipped_and_says_so()
    {
        var evidence = Journal(
            ("write_file", $$"""{"path":"doc.md","content":"{{new string('c', 20_000)}}"}""", "Created")).Describe();

        Assert.Contains("characters of arguments", evidence, StringComparison.Ordinal);
        Assert.Contains("doc.md", evidence, StringComparison.Ordinal);
        Assert.True(evidence.Length < 2_000, $"{evidence.Length} characters");
    }

    /// <summary>
    /// A step with more calls than the budget can even NAME says how many it is not showing. An
    /// evidence list that quietly stops is the entire defect this file is about.
    /// </summary>
    [Fact]
    public void A_list_too_long_to_show_says_how_much_is_missing()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 200; i++)
            journal.Record(1, "read_file", $$"""{"path":"file-{{i}}-with-a-long-enough-name.cs"}""",
                           ActionOutcome.Succeeded, "contents");

        var evidence = journal.Describe(maxChars: 1_500);

        Assert.Contains("200 tool call(s)", evidence, StringComparison.Ordinal);
        Assert.Contains("are not shown here", evidence, StringComparison.Ordinal);
        // The NEWEST survive: a review judges what the step did most recently.
        Assert.Contains("file-199-", evidence, StringComparison.Ordinal);
    }

    // ── the instruction that goes with it ───────────────────────────────────

    /// <summary>
    /// The prompt has to say what a shortened result means, or the next reviewer makes the same
    /// inference from the same shape. The fix is half in the evidence and half in the instruction.
    /// </summary>
    [Fact]
    public void The_reviewer_is_told_that_a_shortened_result_is_not_a_missing_call()
    {
        var instructions = Enactive.Agents.Reviewer.ExecutionSystemPrompt;

        Assert.Contains("EVERY call", instructions, StringComparison.Ordinal);
        Assert.Contains("shortened result is still a call that HAPPENED", instructions, StringComparison.Ordinal);
        Assert.Contains("Judge by the calls listed", instructions, StringComparison.Ordinal);
    }
}
