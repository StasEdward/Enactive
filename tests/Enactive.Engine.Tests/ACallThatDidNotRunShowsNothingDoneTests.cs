namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// Code review of engeen_v4, 2026-09-29, P1: the short step review took a pass whose only citation was a refused
/// send_email - a call being in the list is not the call having happened. A call that was refused, or a tool that
/// failed without running anything, shows nothing done; a process that ran shows what it showed, whatever its exit
/// code, since a failure can be the very result asked for. Deliberately not code: a mail, and a link check that must fail.
/// </summary>
public sealed class ACallThatDidNotRunShowsNothingDoneTests
{
    private static StepVerdictInput Input(params (string Tool, ActionOutcome Outcome, string Output, int? Exit)[] calls)
    {
        var journal = new ExecutionJournal();
        foreach (var (tool, outcome, output, exit) in calls)
            journal.Record(1, tool, "{}", outcome, output, exitCode: exit);
        return new StepVerdictInput("send the report", "Send the report", 1, [], "Sent.", null, [], journal.Describe());
    }

    private static (ReviewVerdict? Verdict, IReadOnlyList<string> Errors) Pass(StepVerdictInput input, string calls)
        => StepVerdictReview.Read($$"""{"verdict":"pass","reason":"the report was sent","calls":[{{calls}}],"files":[]}""", input);

    /// <summary>THE ONE THAT MATTERS: a pass resting only on a refused call goes back, and says why.</summary>
    [Fact]
    public void A_pass_resting_on_a_refused_call_goes_back()
    {
        var (result, errors) = Pass(Input(("send_email", ActionOutcome.Refused, "not permitted", null)), "1");

        Assert.Null(result);
        Assert.Contains(errors, e => e.Contains("[1] did not run or did not do what it was called for", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pass_resting_on_a_tool_that_failed_goes_back()
        => Assert.Null(Pass(Input(("send_email", ActionOutcome.Failed, "SMTP 550: mailbox unavailable", null)), "1").Verdict);

    /// <summary>A process that ran and failed shows what it showed: a check that had to fail, failing.</summary>
    [Fact]
    public void A_process_that_ran_and_failed_can_show_a_step_done()
    {
        var (result, errors) = Pass(Input(("run_command", ActionOutcome.Failed, "2 broken links", 1)), "1");

        Assert.Empty(errors);
        Assert.IsType<ReviewVerdict.Pass>(result);
    }

    /// <summary>A refused call beside one that worked counts for nothing, and costs no second round.</summary>
    [Fact]
    public void A_refused_call_beside_one_that_worked_is_not_counted_and_not_sent_back()
    {
        var (result, errors) = Pass(Input(("send_email", ActionOutcome.Refused, "not permitted", null),
            ("send_email", ActionOutcome.Succeeded, "Sent to the owner with 1 attachment", null)), "1, 2");

        Assert.Empty(errors);
        Assert.IsType<ReviewVerdict.Pass>(result);
    }

    /// <summary>A fail may cite a refused call: what did not happen is what a fail is about.</summary>
    [Fact]
    public void A_fail_may_cite_a_refused_call()
    {
        var (result, errors) = StepVerdictReview.Read("""{"verdict":"fail","reason":"the mail was refused","calls":[1],"files":[]}""",
            Input(("send_email", ActionOutcome.Refused, "not permitted", null)));

        Assert.Empty(errors);
        Assert.IsType<ReviewVerdict.Fail>(result);
    }
}

/// <summary>
/// Code review of engeen_v4, 2026-09-29, P2: the short step review took any answer whose JSON read cleanly - one cut off
/// at its length limit, or one that called for a tool, included - as the verdict. Neither is a finished one: it goes
/// back once, and with no finished answer after that the verdict is unavailable. Deliberately not code: a mail.
/// </summary>
public sealed class AnUnfinishedReviewIsNoVerdictTests
{
    private static StepVerdictInput Input()
    {
        var journal = new Enactive.Core.Execution.ExecutionJournal();
        journal.Record(1, "send_email", "{}", Enactive.Core.Execution.ActionOutcome.Succeeded, "Sent to the owner with 1 attachment");
        return new StepVerdictInput("send the report", "Send the report", 1, [], "Sent.", null, [], journal.Describe());
    }

    private const string PassJson = """{"verdict":"pass","reason":"the mail was sent","calls":[1],"files":[]}""";

    private static Task<ReviewResult> Review(params Turn[] turns)
        => StepVerdictReview.RunAsync(Input(), new FakeChatProvider(turns), "reviewer", null, CancellationToken.None);

    /// <summary>THE ONE THAT MATTERS: a pass cut off at its length limit is sent back, not taken.</summary>
    [Fact]
    public async Task A_pass_cut_off_at_its_length_is_sent_back()
    {
        var provider = new FakeChatProvider(Turn.Says(PassJson) with { FinishReason = "length" }, Turn.Says(PassJson));
        var result = await StepVerdictReview.RunAsync(Input(), provider, "reviewer", null, CancellationToken.None);

        Assert.IsType<ReviewVerdict.Pass>(result.Verdict);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("cut off at its length limit", provider.Requests[1].Messages.Last().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cut_off_twice_is_no_verdict()
    {
        var result = await Review(Turn.Says(PassJson) with { FinishReason = "max_tokens" }, Turn.Says(PassJson) with { FinishReason = "length" });

        Assert.IsType<ReviewVerdict.Unavailable>(result.Verdict);
    }

    [Fact]
    public async Task An_answer_that_calls_a_tool_is_no_verdict_either()
    {
        var call = new Enactive.Core.Tools.ToolCall("t1", "read_file", """{"path":"report.md"}""");
        var result = await Review(new Turn(PassJson, [call]), new Turn(PassJson, [call]));

        Assert.IsType<ReviewVerdict.Unavailable>(result.Verdict);
    }
}
