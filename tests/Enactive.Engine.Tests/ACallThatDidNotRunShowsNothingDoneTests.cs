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

    private static (ReviewResult? Result, IReadOnlyList<string> Errors) Pass(StepVerdictInput input, string calls)
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
        => Assert.Null(Pass(Input(("send_email", ActionOutcome.Failed, "SMTP 550: mailbox unavailable", null)), "1").Result);

    /// <summary>A process that ran and failed shows what it showed: a check that had to fail, failing.</summary>
    [Fact]
    public void A_process_that_ran_and_failed_can_show_a_step_done()
    {
        var (result, errors) = Pass(Input(("run_command", ActionOutcome.Failed, "2 broken links", 1)), "1");

        Assert.Empty(errors);
        Assert.True(result!.Pass);
    }

    /// <summary>A refused call beside one that worked counts for nothing, and costs no second round.</summary>
    [Fact]
    public void A_refused_call_beside_one_that_worked_is_not_counted_and_not_sent_back()
    {
        var (result, errors) = Pass(Input(("send_email", ActionOutcome.Refused, "not permitted", null),
            ("send_email", ActionOutcome.Succeeded, "Sent to the owner with 1 attachment", null)), "1, 2");

        Assert.Empty(errors);
        Assert.True(result!.Pass);
    }

    /// <summary>A fail may cite a refused call: what did not happen is what a fail is about.</summary>
    [Fact]
    public void A_fail_may_cite_a_refused_call()
    {
        var (result, errors) = StepVerdictReview.Read("""{"verdict":"fail","reason":"the mail was refused","calls":[1],"files":[]}""",
            Input(("send_email", ActionOutcome.Refused, "not permitted", null)));

        Assert.Empty(errors);
        Assert.False(result!.Pass);
    }
}
