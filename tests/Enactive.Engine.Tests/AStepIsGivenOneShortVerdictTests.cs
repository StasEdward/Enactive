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

    private static async Task<(List<WorkEvent> Events, FakeChatProvider Worker, FakeChatProvider Reviewer)> Run(
        Turn[] reviews, params Turn[] more)
    {
        using var fx = new EngineFixture();
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
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer), "check the disks and write a report");
        return (events, worker, reviewer);
    }

    /// <summary>THE ONE THAT MATTERS: every step passes its short verdict, and that is the task done - no review after it.</summary>
    [Fact]
    public async Task Every_step_passed_is_the_task_done()
    {
        var (events, _, reviewer) = await Run([Pass(), Pass("the report matches the listing", 3)]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(2, reviewer.Requests.Count);                                                  // one per step, none after
        var asked = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("THIS STEP (1): Write the disk report", asked, StringComparison.Ordinal);
        Assert.Contains("Other steps of the plan (theirs, not this step's): 2. Check the report", asked, StringComparison.Ordinal);
        Assert.Contains("--- report.md", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("report_checks", asked, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary.Contains("PASS (Step review)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fail_says_what_to_put_right_and_the_step_is_done_again()
    {
        var (events, worker, _) = await Run([Fail("the report does not say how big the disk is"), Pass(), Pass(call: 3)],
            Turn.Calls1("write_file", """{"path":"report.md","content":"C: 120 GB free of 500 GB total"}""", "w2"), Turn.Says("Added the size."));

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains(worker.Requests, r => r.Messages.Any(m => m.Content?.Contains("the report does not say how big the disk is", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task A_pass_that_cites_nothing_shown_is_corrected()
    {
        var (events, _, reviewer) = await Run([Pass(call: 99), Pass(), Pass(call: 3)]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains("call 99 is not in the evidence shown", reviewer.Requests[1].Messages.Last().Content, StringComparison.Ordinal);
    }

    private static Turn PassCiting(string file, int call = 3)
        => Turn.Says($$"""{"verdict":"pass","reason":"the report is right","calls":[{{call}}],"files":["{{file}}"]}""");

    /// <summary>
    /// Every disk run of 2026-09-29: the step that mailed the report cited it - a file an earlier step wrote, not shown to
    /// this step's review - was told "not among the FILES shown", and answered the same pass again. A file the calls
    /// shown work with counts for nothing but costs no second round.
    /// </summary>
    [Fact]
    public async Task A_file_the_calls_work_with_but_the_step_did_not_write_costs_no_second_round()
    {
        var (events, _, reviewer) = await Run([Pass(), PassCiting("report.md")]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(2, reviewer.Requests.Count);
    }

    /// <summary>A file no call mentions at all may be one the step never made: that still goes back.</summary>
    [Fact]
    public async Task A_file_no_call_mentions_still_goes_back()
    {
        var (events, _, reviewer) = await Run([Pass(), PassCiting("summary.md"), Pass(call: 3)]);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(3, reviewer.Requests.Count);
        Assert.Contains("'summary.md' is not among the FILES shown", reviewer.Requests[2].Messages.Last().Content, StringComparison.Ordinal);
    }

    /// <summary>A pass whose only citation is such a file has shown nothing: it goes back too.</summary>
    [Fact]
    public async Task A_pass_citing_only_a_file_not_shown_still_goes_back()
    {
        var (_, _, reviewer) = await Run(
            [Pass(), Turn.Says("""{"verdict":"pass","reason":"the report is right","calls":[],"files":["report.md"]}"""), Pass(call: 3)]);

        Assert.Equal(3, reviewer.Requests.Count);
        Assert.Contains("a pass cites the calls or files that show the step done", reviewer.Requests[2].Messages.Last().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_that_cannot_answer_leaves_the_step_unconfirmed()
    {
        var (events, _, _) = await Run([Turn.Says("looks fine"), Turn.Says("still fine"), Pass(call: 3)]);

        Assert.Equal(StepOutcomeKind.DoneUnverified, events.First(e => e.Kind == EventKind.StepCompleted).StepOutcome());
    }

    /// <summary>Not complete is said as a list: every step not confirmed, with the part of the request it answers for.</summary>
    [Fact]
    public async Task What_is_not_complete_is_listed()
    {
        var (events, _, _) = await Run([Fail("the report is empty"), Fail("the report is still empty")]);

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        var reason = events.Last().OutcomeReason()!;
        Assert.StartsWith("Not complete - [1] Write the disk report", reason, StringComparison.Ordinal);
        Assert.Contains("review rejected: review not passed: the report is still empty", reason, StringComparison.Ordinal);
        Assert.Contains("[2] Check the report", reason, StringComparison.Ordinal);
        Assert.Contains("- skipped", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Run 1ec9e8: a total the worker added in its head was 70 GB short, and the step reviews passed it with every row in
    /// front of them. The review is told to work such a figure out itself - always: it was a setting, on in the
    /// application's settings and off in an engine built without them, so the two were reviewed differently.
    /// </summary>
    [Fact]
    public async Task A_derived_figure_is_worked_out_by_the_review()
    {
        var (_, _, reviewer) = await Run([Pass(), Pass("the report matches the listing", 3)]);

        var instruction = reviewer.Requests[0].Messages[0].Content!;
        Assert.Contains("a total, a difference, a percentage, an average - is a claim too", instruction, StringComparison.Ordinal);
        Assert.Contains("work it out from the values the calls show, and fail it if it is wrong", instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// Run 0a2be9, 2026-10-05: "the five processes that use the most memory" were picked by the worker from 370 rows in its
    /// head, three of five wrong; the review was shown the result with its middle cut out, checked that the five values were
    /// in it, and passed. A selection from a result is derived like a total, and where the rows it depends on are not
    /// shown it is not shown.
    /// </summary>
    [Fact]
    public async Task A_selection_from_a_result_is_checked_against_every_row_and_not_passed_on_a_shortened_one()
    {
        var (_, _, reviewer) = await Run([Pass(), Pass("the report matches the listing", 3)]);

        var instruction = reviewer.Requests[0].Messages[0].Content!;
        Assert.Contains("the largest, the first five, the ones that match, that none does", instruction, StringComparison.Ordinal);
        Assert.Contains("the selection is not shown: fail it", instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// Benchmark scenario build-error, 2026-09-30: a change the request did not ask for, to what it said to leave alone,
    /// passed as "out of scope but harmless". The review is told that is not a detail.
    /// </summary>
    [Fact]
    public async Task The_review_is_told_a_change_the_request_did_not_ask_for_is_not_a_detail()
    {
        var (_, _, reviewer) = await Run([Pass(), Pass("the report matches the listing", 3)]);

        var instruction = reviewer.Requests[0].Messages[0].Content!;
        Assert.Contains("A change the step made that the request did not ask for is not a detail", instruction, StringComparison.Ordinal);
        Assert.Contains("a step that changed it fails - name the change", instruction, StringComparison.Ordinal);
    }

    /// <summary>A call that answered with an absence is marked NOTHING THERE; the review is told that is no error.</summary>
    [Fact]
    public async Task The_review_is_told_nothing_there_is_no_error()
    {
        var (_, _, reviewer) = await Run([Pass(), Pass("the report matches the listing", 3)]);

        var instruction = reviewer.Requests[0].Messages[0].Content!;
        Assert.Contains("A call marked NOTHING THERE ran and answered", instruction, StringComparison.Ordinal);
        Assert.Contains("it is no error", instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a step reports done or so - by itself or by a step before it - stands only on a call made after the work it
    /// rests on; what would follow from the work, and the values a step handed on, are not shown. Told instead that a step
    /// reporting nothing to do needs "a call it made", one reviewer passed a step whose tests nobody ran after the fix, and
    /// another failed steps whose tests the step before had run (2026-10-01).
    /// </summary>
    [Fact]
    public async Task The_review_is_told_a_result_stands_on_a_call_made_after_the_work()
    {
        var (_, _, reviewer) = await Run([Pass(), Pass("the report matches the listing", 3)]);

        var instruction = reviewer.Requests[0].Messages[0].Content!.Replace("\r\n", " ").Replace("\n", " ");
        Assert.Contains("A step that reports something done or so - by itself, or by a step before it - has it only where a call shows it, made after the work it rests on", instruction, StringComparison.Ordinal);
        Assert.Contains("what would follow from the work is not shown, and neither are the values a step handed on", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("A step that reports nothing needed doing", instruction, StringComparison.Ordinal);
    }

}
