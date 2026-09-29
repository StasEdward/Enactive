namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// The user's model, 2026-09-29: a step is given one short verdict - did it do what it is for, and is its report true? -
/// pass or fail, on what the recorded calls and the files show; and the task is done when every step is and the engine's
/// own checks are green. No review of every sentence, nothing deferred to a review of the whole run after it. A task that
/// took five minutes on an earlier version took twenty and failed on that machinery. Deliberately not code: a disk report.
/// </summary>
public sealed class AStepIsGivenOneShortVerdictTests
{
    private const string Plan = """
        {"disposition":"task","title":"disk report",
         "steps":[{"title":"Write the disk report","dependsOn":[]},{"title":"Check the report","dependsOn":[0]}]}
        """;

    private static Turn Pass(string reason = "the report is written and says what the listing shows", int call = 1)
        => Turn.Says($$"""{"verdict":"pass","reason":"{{reason}}","calls":[{{call}}],"files":[]}""");

    private static Turn Fail(string reason) => Turn.Says($$"""{"verdict":"fail","reason":"{{reason}}","calls":[],"files":[]}""");

    private static Task<(List<WorkEvent> Events, FakeChatProvider Worker, FakeChatProvider Reviewer)> Run(bool shortReview, Turn[] reviews,
        params Turn[] more) => Run(shortReview, false, reviews, more);

    private static async Task<(List<WorkEvent> Events, FakeChatProvider Worker, FakeChatProvider Reviewer)> Run(bool shortReview,
        bool derivedFigures, Turn[] reviews, params Turn[] more)
    {
        using var fx = new EngineFixture { ShortReview = shortReview, CheckDerivedFigures = derivedFigures };
        fx.Write("disks.txt", "C: 120 GB free of 500 GB");
        var worker = new FakeChatProvider(
            [Turn.Says(Plan),
             Turn.Calls1("read_file", """{"path":"disks.txt"}""", "r1"),
             Turn.Calls1("write_file", """{"path":"report.md","content":"C: 120 GB free of 500 GB"}""", "w1"),
             Turn.Says("Report written."),
             Turn.Calls1("read_file", """{"path":"report.md"}""", "r2"),
             Turn.Says("The report is right."),
             .. more]) { WhenExhausted = Turn.Says("Done.") };
        var reviewer = new FakeChatProvider(reviews);
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true), "check the disks and write a report");
        return (events, worker, reviewer);
    }

    /// <summary>THE ONE THAT MATTERS: every step passes its short verdict, and that is the task done - no review after it.</summary>
    [Fact]
    public async Task Every_step_passed_is_the_task_done()
    {
        var (events, _, reviewer) = await Run(true, [Pass(), Pass("the report matches the listing", 3)]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(2, reviewer.Requests.Count);                                                  // one per step, none after
        var asked = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("THIS STEP (1): Write the disk report", asked, StringComparison.Ordinal);
        Assert.Contains("Other steps of the plan (theirs, not this step's): Check the report", asked, StringComparison.Ordinal);
        Assert.Contains("--- report.md", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("report_checks", asked, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary.Contains("PASS (Step review)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fail_says_what_to_put_right_and_the_step_is_done_again()
    {
        var (events, worker, _) = await Run(true, [Fail("the report does not say how big the disk is"), Pass(), Pass(call: 3)],
            Turn.Calls1("write_file", """{"path":"report.md","content":"C: 120 GB free of 500 GB total"}""", "w2"), Turn.Says("Added the size."));

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains(worker.Requests, r => r.Messages.Any(m => m.Content?.Contains("the report does not say how big the disk is", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task A_pass_that_cites_nothing_shown_is_corrected()
    {
        var (events, _, reviewer) = await Run(true, [Pass(call: 99), Pass(), Pass(call: 3)]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains("call 99 is not in the evidence shown", reviewer.Requests[1].Messages.Last().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_that_cannot_answer_leaves_the_step_unconfirmed()
    {
        var (events, _, _) = await Run(true, [Turn.Says("looks fine"), Turn.Says("still fine"), Pass(call: 3)]);

        Assert.Equal(StepOutcomeKind.DoneUnverified, events.First(e => e.Kind == EventKind.StepCompleted).StepOutcome());
    }

    /// <summary>Not complete is said as a list: every step not confirmed, with the part of the request it answers for.</summary>
    [Fact]
    public async Task What_is_not_complete_is_listed()
    {
        var (events, _, _) = await Run(true, [Fail("the report is empty"), Fail("the report is still empty")]);

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        var reason = events.Last().OutcomeReason()!;
        Assert.StartsWith("Not complete - [1] Write the disk report", reason, StringComparison.Ordinal);
        Assert.Contains("review rejected: review not passed: the report is still empty", reason, StringComparison.Ordinal);
        Assert.Contains("[2] Check the report", reason, StringComparison.Ordinal);
        Assert.Contains("- skipped", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Run 1ec9e8: a total the worker added in its head was 70 GB short, and the step reviews passed it with every row in
    /// front of them. Switched on, the review is told to work such a figure out itself; off, it is not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_derived_figure_is_worked_out_by_the_review_when_switched_on(bool on)
    {
        var (_, _, reviewer) = await Run(true, on, [Pass(), Pass("the report matches the listing", 3)]);

        var instruction = reviewer.Requests[0].Messages[0].Content!;
        Assert.Equal(on, instruction.Contains("a total, a difference, a percentage, an average - is a claim too", StringComparison.Ordinal));
        Assert.Equal(on, instruction.Contains("work it out from the values the calls show, and fail it if it is wrong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Off_the_step_review_is_the_one_before()
    {
        var (_, _, reviewer) = await Run(false, [Verdicts.Pass(), Verdicts.Pass()]);

        Assert.DoesNotContain("THIS STEP (1)", string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)), StringComparison.Ordinal);
    }
}
