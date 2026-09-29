namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Phase 9, run feed29 (2026-09-29): a step's review could not establish per-class coverage in a cut excerpt, the next
/// step showed it, and the run stayed Incomplete - no one judged the run as a whole. Now a run short of Completed only on
/// steps DONE, NOT VERIFIED is reviewed as a whole: each open question is answered on the run's evidence, cited, and the
/// engine reads the answer - all confirmed and every obligation met is Completed, anything refuted Failed, the rest stays
/// Incomplete. Deliberately not code: boxes on a warehouse shelf and a summary of them.
/// </summary>
public sealed class TheRunIsReviewedAsAWholeTests
{
    private const string Plan = """
        {"disposition":"task","title":"stocktake",
         "steps":[{"title":"Count the boxes","dependsOn":[]},{"title":"Write the summary","dependsOn":[0]}]}
        """;

    private const string Request = "Count the boxes on shelf 17 and write a summary.";

    private static Turn Undecided()
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("shelf listed", 1)).Text!)!;
        answer["assessments"]!["verification"]!["verdict"] = "unknown";
        answer["assessments"]!["verification"]!["reason"] = "The count of 42 is not visible in what this step showed";
        return Turn.Says(answer.ToJsonString());
    }

    private static Turn Rejected()
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("counted")).Text!)!;
        answer["report_checks"] = new JsonArray(new JsonObject {
            ["source_id"] = "worker-report", ["fragment_id"] = "F1", ["kind"] = "inferred",
            ["verdict"] = "fail", ["reason"] = "The count in the report is wrong",
            ["calls"] = new JsonArray(), ["obligation_ids"] = new JsonArray()
        });
        answer["repairs"] = new JsonArray(Verdicts.Repair("$.report_checks[0]", "Incorrect count", "State the count shown", "worker-report", "F1"));
        return Turn.Says(answer.ToJsonString());
    }

    private static Turn StepTwoPasses() => Verdicts.Combined(Verdicts.NotByAnyCall("the summary is written from step 1's count"), "S2");

    private static Turn Judged(string item, string obligation = "pass", string inconsistencies = "[]", int call = 1)
        => Turn.Says($$"""
            {"open_items":[{"id":"Q1","verdict":"{{item}}","reason":"the shelf listing shows 42 boxes","calls":[{{call}}]}],
             "obligations":[{"id":"O001","verdict":"{{obligation}}","reason":"counted and summed up","calls":[1]}],
             "inconsistencies":{{inconsistencies}},"notes":""}
            """);

    private static async Task<(List<WorkEvent> Events, FakeChatProvider Reviewer)> Run(bool taskReview, params Turn[] reviews)
    {
        using var fx = new EngineFixture { TaskReview = taskReview };
        fx.Write("stock/shelf17.txt", "shelf 17: 42 boxes");
        var worker = new ByStepChatProvider(Plan)
            .Step("Count the boxes", Turn.Calls1("read_file", """{"path":"stock/shelf17.txt"}""", "r1"), Turn.Says("Shelf 17 holds 42 boxes."))
            .Step("Write the summary", Turn.Says("Summary: 42 boxes on shelf 17."));
        var reviewer = new FakeChatProvider(reviews);
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewContent: false), Request);
        return (events, reviewer);
    }

    /// <summary>THE ONE THAT MATTERS: what one step could not establish, the run shows - and the run is Completed on it.</summary>
    [Fact]
    public async Task An_open_question_the_run_answers_makes_it_completed()
    {
        var (events, reviewer) = await Run(true, Undecided(), StepTwoPasses(), Judged("confirmed"));

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.StartsWith("Task review: what the steps left open is shown by the run", events.Last().OutcomeReason(), StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary.StartsWith("[1] verification: confirmed by the task review", StringComparison.Ordinal));
        var asked = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.Contains("Q1 (step 1, Count the boxes) verification: The count of 42 is not visible", asked, StringComparison.Ordinal);
        Assert.Contains("--- stock/shelf17.txt", asked + "--- stock/shelf17.txt", StringComparison.Ordinal);
        Assert.Contains("[1]", asked, StringComparison.Ordinal);                                   // the calls of the whole run
    }

    [Theory]
    [InlineData("refuted", "pass", "[]", RunOutcomeKind.Failed, "Task review: refuted")]
    [InlineData("confirmed", "fail", "[]", RunOutcomeKind.Failed, "Task review: O001 not met")]
    [InlineData("confirmed", "pass", """[{"finding":"the summary says 40, the listing 42","calls":[1]}]""", RunOutcomeKind.Failed, "Task review: inconsistent")]
    [InlineData("still-unknown", "pass", "[]", RunOutcomeKind.Incomplete, "Task review: still not established")]
    public async Task The_engine_reads_the_answer(string item, string obligation, string inconsistencies, RunOutcomeKind outcome, string reason)
    {
        var (events, _) = await Run(true, Undecided(), StepTwoPasses(), Judged(item, obligation, inconsistencies));

        Assert.Equal(outcome, events.Last().Outcome());
        Assert.StartsWith(reason, events.Last().OutcomeReason(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_citation_to_a_call_not_shown_is_refused_and_corrected()
    {
        var (events, reviewer) = await Run(true, Undecided(), StepTwoPasses(), Judged("confirmed", call: 99), Judged("confirmed"));

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains("call 99 is not in the evidence shown", reviewer.Requests[^1].Messages.Last().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_review_that_cannot_answer_changes_nothing()
    {
        var (events, _) = await Run(true, Undecided(), StepTwoPasses(), Turn.Says("I think it is fine."), Turn.Says("Still fine."));

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains(events, e => e.Summary.StartsWith("Task review unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Off_there_is_no_task_review()
    {
        var (events, reviewer) = await Run(false, Undecided(), StepTwoPasses(), Judged("confirmed"));

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal(2, reviewer.Requests.Count);
    }

    /// <summary>A long file, too many files, a long listing: each says it was cut, and how much.</summary>
    [Fact]
    public async Task What_the_task_review_is_shown_says_where_it_was_cut()
    {
        using var fx = new EngineFixture { TaskReview = true };
        fx.Write("stock/shelf17.txt", "shelf 17: 42 boxes\n" + new string('x', 30_000));
        var big = new string('y', 15_000);
        var worker = new ByStepChatProvider(Plan)
            .Step("Count the boxes", Turn.Calls1("read_file", """{"path":"stock/shelf17.txt"}""", "r1"), Turn.Says("Shelf 17 holds 42 boxes."))
            .Step("Write the summary",
                Turn.Calls1("write_file", $$"""{"path":"out/a.md","content":"{{big}}"}""", "w1"),
                Turn.Calls1("write_file", $$"""{"path":"out/b.md","content":"{{big}}"}""", "w2"),
                Turn.Calls1("write_file", $$"""{"path":"out/c.md","content":"{{big}}"}""", "w3"),
                Turn.Calls1("write_file", $$"""{"path":"out/d.md","content":"{{big}}"}""", "w4"),
                Turn.Calls1("write_file", $$"""{"path":"out/e.md","content":"{{big}}"}""", "w5"),
                Turn.Says("Summary: 42 boxes on shelf 17."));
        var reviewer = new FakeChatProvider(Undecided(), StepTwoPasses(), Judged("still-unknown"));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewContent: false), Request);

        var asked = reviewer.Requests[^1].Messages[1].Content!;
        Assert.Contains("characters not shown here; the end follows", asked, StringComparison.Ordinal);    // a file, and the listing
        Assert.Contains("(not shown: the room for files is spent)", asked, StringComparison.Ordinal);
    }

    /// <summary>A step rejected by its review is a verdict, not an open question: the run is not reviewed as a whole.</summary>
    [Fact]
    public async Task A_rejected_step_is_not_reopened()
    {
        var (events, reviewer) = await Run(true, Rejected(), Rejected(), Judged("confirmed"));

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.DoesNotContain(events, e => e.Summary.StartsWith("Task review", StringComparison.Ordinal));
    }
}
