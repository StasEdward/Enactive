namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// Reported 2026-09-07 22:22, on a different project and with the reviewer moved to the 31B model.
/// The tests were green. Step 2 was rejected twice anyway, and the reviewer named the cause itself:
///
/// <para><c>"The agent reports 'Total tests: 22, Passed: 22, Failed: 0', but the provided tool output
/// for 'dotnet test' is truncated and does not contain these specific numbers. The agent fabricated
/// the test summary."</c></para>
///
/// <para>The numbers were real. They were in the line after the cut. <c>dotnet test</c> puts its
/// restore and build noise first and its verdict last, and a shortened result kept the first N
/// characters — so the reviewer was handed the part with no answer in it and asked whether the
/// answer was true.</para>
///
/// <para>§8i decided which CALLS survive the budget: all of them, only outputs are shortened. This
/// is the same question one level down — which PART of an output survives — and it had the same
/// wrong answer. <see cref="Enactive.Agents.LogAnalyst"/> already got this right for the same
/// reason, and says so: "taking the first N characters, the obvious implementation, would reliably
/// hand over the part with no failures in it and then be asked what failed."</para>
///
/// <para>A shortened result now keeps its start AND its end, with the cut marked between them.</para>
/// </summary>
public sealed class ResultTailTests
{
    /// <summary>A dotnet test run, to shape: noise, then the line that matters.</summary>
    private const string Summary = "Passed!  - Failed: 0, Passed: 22, Skipped: 0, Total: 22";

    private static string TestOutput(int noise = 20_000)
        => "exit code 0\n----- command output (this is the result) -----\n"
         + "  Determining projects to restore...\n"
         + string.Join("\n", Enumerable.Range(0, noise / 40).Select(i => $"  Restored package {i}"))
         + "\n" + Summary;

    private static ExecutionJournal Journal(string tool, string output)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, tool, """{"command":"dotnet test"}""", ActionOutcome.Succeeded, output);
        return journal;
    }

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>The one that matters: the verdict is in the evidence.</summary>
    [Fact]
    public void The_end_of_a_long_result_survives()
    {
        var evidence = Journal("run_command", TestOutput()).Describe();

        Assert.Contains(Summary, evidence, StringComparison.Ordinal);
    }

    /// <summary>And so does enough of the start to recognise what ran.</summary>
    [Fact]
    public void The_start_survives_too()
    {
        var evidence = Journal("run_command", TestOutput()).Describe();

        Assert.Contains("Determining projects to restore", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cut says it is a cut, and where. A gap with nothing marking it is a result that reads as
    /// complete and is not — the failure this whole file is about, one level up.
    /// </summary>
    [Fact]
    public void The_cut_is_marked_and_says_how_much_is_missing()
    {
        var evidence = Journal("run_command", TestOutput()).Describe();

        Assert.Contains("cut from the middle", evidence, StringComparison.Ordinal);
        Assert.Contains("the end follows", evidence, StringComparison.Ordinal);
    }

    /// <summary>And the header tells the reader that is what to expect.</summary>
    [Fact]
    public void The_header_says_the_end_is_always_there()
    {
        var evidence = Journal("run_command", TestOutput()).Describe();

        Assert.Contains("keeps its START and its END", evidence, StringComparison.Ordinal);
        Assert.Contains("closing summary is always here", evidence, StringComparison.Ordinal);
    }

    /// <summary>Whatever the budget, down to where two pieces stop fitting.</summary>
    [Theory]
    [InlineData(600)]
    [InlineData(1_000)]
    [InlineData(3_000)]
    [InlineData(6_000)]
    public void The_verdict_survives_at_any_usable_budget(int maxChars)
        => Assert.Contains(Summary, Journal("run_command", TestOutput()).Describe(maxChars: maxChars),
                           StringComparison.Ordinal);

    /// <summary>
    /// The reported step, to scale: ten calls sharing one budget, one of them a huge test run. Its
    /// verdict still arrives.
    /// </summary>
    [Fact]
    public void One_long_result_among_many_still_ends_where_it_ended()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 8; i++)
            journal.Record(1, "read_file", $$"""{"path":"src/File{{i}}.cs"}""",
                           ActionOutcome.Succeeded, new string('x', 6_000));
        journal.Record(1, "write_file", """{"path":"tests/NewTests.cs"}""",
                       ActionOutcome.Succeeded, "Created 'tests/NewTests.cs' (4210 bytes).");
        journal.Record(1, "run_command", """{"command":"dotnet test"}""",
                       ActionOutcome.Succeeded, TestOutput());

        var evidence = journal.Describe();

        Assert.Contains(Summary, evidence, StringComparison.Ordinal);
        Assert.Contains("10 tool call(s)", evidence, StringComparison.Ordinal);
    }

    /// <summary>A failure's tail matters just as much — the error is at the end of a build too.</summary>
    [Fact]
    public void A_failed_command_keeps_its_ending_as_well()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", """{"command":"dotnet build"}""", ActionOutcome.Failed,
                       new string('n', 20_000) + "\nBuild FAILED. 3 Error(s)");

        Assert.Contains("Build FAILED. 3 Error(s)", journal.Describe(), StringComparison.Ordinal);
    }

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>A result that fits is untouched — no marker, no cut, exactly what happened.</summary>
    [Fact]
    public void A_result_that_fits_is_left_exactly_alone()
    {
        var evidence = Journal("run_command", "exit code 0\nBuild succeeded.").Describe();

        Assert.Contains("<- exit code 0\nBuild succeeded.", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("cut from the middle", evidence, StringComparison.Ordinal);
    }

    /// <summary>The labels still lead the result they belong to — the head is what carries them.</summary>
    [Theory]
    [InlineData(ActionOutcome.Failed, "ERROR:")]
    [InlineData(ActionOutcome.Refused, "REFUSED:")]
    [InlineData(ActionOutcome.Answered, "NOTHING THERE:")]
    public void A_label_is_still_the_first_thing_shown(ActionOutcome outcome, string label)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", """{"command":"x"}""", outcome, new string('y', 20_000));

        Assert.Contains("<- " + label, journal.Describe(), StringComparison.Ordinal);
    }

    /// <summary>And the evidence still obeys its budget with two pieces per result instead of one.</summary>
    [Fact]
    public void The_evidence_still_fits_its_budget()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 12; i++)
            journal.Record(1, "run_command", $$"""{"command":"dotnet test {{i}}"}""",
                           ActionOutcome.Succeeded, TestOutput(40_000));

        var evidence = journal.Describe(maxChars: 3_000);

        Assert.True(evidence.Length <= 3_600, $"{evidence.Length} characters");
    }
}
