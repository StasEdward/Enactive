namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 21:43, and it is the previous fix left half done.
///
/// <para>§9m stopped the engine counting a read past the end of a file against a step. It did not
/// tell the JOURNAL, which went on recording those calls as failures, or the REVIEWER, which reads
/// the journal and is instructed to fail work whose report does not account for an error. So the
/// step survived the engine and died at the review:</para>
///
/// <para><c>"The agent reported reading the Program.cs file multiple times with different offsets,
/// but the evidence shows errors for offsets 800 and 400 being past the end of the file. The
/// agent's report does not account for these errors."</c></para>
///
/// <para>The reviewer applied its instructions correctly to evidence that said ERROR. Nothing had
/// gone wrong: the model was paging a file of unknown length, and being told where it ends is the
/// answer to that. <b>Forgiving something in one half of the system while holding it against the
/// work in the other is worse than either alone</b> — the step now fails for a reason the engine
/// has already decided is not a reason.</para>
///
/// <para>So the record itself distinguishes them. <c>ActionOutcome.Answered</c> renders as
/// <c>NOTHING THERE</c> rather than <c>ERROR</c>, and the reviewer is told what that label means.
/// A failure is still a failure and still says ERROR.</para>
/// </summary>
public sealed class AnsweredInEvidenceTests
{
    private static ExecutionJournal Journal(params (string Tool, string Args, ActionOutcome How, string Out)[] actions)
    {
        var journal = new ExecutionJournal();
        foreach (var (tool, args, how, output) in actions)
            journal.Record(1, tool, args, how, output);
        return journal;
    }

    /// <summary>The step from the log: read the file, then ask what is past the end of it.</summary>
    private static ExecutionJournal TheReportedStep()
        => Journal(
            ("read_file", """{"path":"tests/ParserSmokeTest/Program.cs"}""",
             ActionOutcome.Succeeded, "using System;\n// … the file"),
            ("read_file", """{"offset":800,"path":"tests/ParserSmokeTest/Program.cs"}""",
             ActionOutcome.Answered, "'tests/ParserSmokeTest/Program.cs' has 263 line(s); offset 800 is past the end."),
            ("read_file", """{"offset":400,"path":"tests/ParserSmokeTest/Program.cs"}""",
             ActionOutcome.Answered, "'tests/ParserSmokeTest/Program.cs' has 263 line(s); offset 400 is past the end."));

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>The one that matters: what answered is not labelled as something that went wrong.</summary>
    [Fact]
    public void An_answer_is_not_written_down_as_an_error()
    {
        var evidence = TheReportedStep().Describe().Text;

        Assert.DoesNotContain("ERROR", evidence, StringComparison.Ordinal);
        Assert.Contains("NOTHING THERE", evidence, StringComparison.Ordinal);
    }

    /// <summary>And what it found is still there — the line count is the useful part.</summary>
    [Fact]
    public void What_the_lookup_found_is_still_in_the_evidence()
        => Assert.Contains("has 263 line(s)", TheReportedStep().Describe().Text, StringComparison.Ordinal);

    /// <summary>The call is still listed. This is evidence; nothing disappears from it.</summary>
    [Fact]
    public void The_call_is_still_listed()
    {
        var evidence = TheReportedStep().Describe().Text;

        Assert.Contains("3 tool call(s)", evidence, StringComparison.Ordinal);
        Assert.Contains("\"offset\":800", evidence, StringComparison.Ordinal);
    }

    /// <summary>End to end: the reported step's evidence reaches the reviewer without an ERROR in it.</summary>
    [Fact]
    public async Task The_reported_step_is_reviewed_on_evidence_that_says_nothing_went_wrong()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", string.Join("\n", Enumerable.Range(1, 263).Select(i => $"line {i}")));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"read the tests"}"""),
                    Turn.Calls1("read_file", """{"path":"Program.cs"}"""),
                    Turn.Calls1("read_file", """{"path":"Program.cs","offset":800}""", "c2"),
                    Turn.Says("Program.cs is 263 lines; I have all of it.")),
                EngineFixture.Role("developer"),
                router: Routers.WithReviewer(), reviewProvider: reviewer),
            "read the test file");

        var prompt = reviewer.Requests.Last().Messages.Last().Content ?? "";

        Assert.Contains("NOTHING THERE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR:", prompt, StringComparison.Ordinal);
    }

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>A real failure still says ERROR. This is a new label, not a softer one for everything.</summary>
    [Fact]
    public void A_failure_is_still_an_error()
    {
        var evidence = Journal(
            ("run_command", """{"command":"dotnet build"}""", ActionOutcome.Failed, "error CS1002")).Describe().Text;

        Assert.Contains("ERROR: error CS1002", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING THERE", evidence, StringComparison.Ordinal);
    }

    /// <summary>A refusal is still a refusal — three outcomes, three words.</summary>
    [Fact]
    public void A_refusal_is_still_a_refusal()
    {
        var evidence = Journal(
            ("git", """{"args":["push"]}""", ActionOutcome.Refused, "blocked by the permission policy")).Describe().Text;

        Assert.Contains("REFUSED", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING THERE", evidence, StringComparison.Ordinal);
    }

    /// <summary>And a success is still bare output, with no label at all.</summary>
    [Fact]
    public void A_success_carries_no_label()
    {
        var evidence = Journal(
            ("read_file", """{"path":"a.cs"}""", ActionOutcome.Succeeded, "namespace A;")).Describe().Text;

        Assert.Contains("<- namespace A;", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING THERE", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", evidence, StringComparison.Ordinal);
    }

}
