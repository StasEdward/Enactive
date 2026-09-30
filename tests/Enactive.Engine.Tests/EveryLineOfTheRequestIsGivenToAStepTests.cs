namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// With one short review per step and none of the whole run after them, a line of the request no step is given is checked
/// by no one - the earlier review of the whole run was what caught it (code review of the move to one short review,
/// 2026-09-30). So the planner is asked once to give every line to a step, and each step's review is shown the lines it
/// was given: it judges the step against them, not against its title alone.
/// </summary>
public sealed class EveryLineOfTheRequestIsGivenToAStepTests
{
    private const string Request = "write a.txt with the word one\nwrite b.txt with the word two";

    private static string Plan(string second) => $$"""
        {"disposition":"task","title":"two files",
         "steps":[{"title":"Write a.txt","dependsOn":[],"obligations":["O001"]},
                  {"title":"Write b.txt","dependsOn":[0],"obligations":{{second}}}]}
        """;

    private static Turn Pass(int call) => Turn.Says($$"""{"verdict":"pass","reason":"the file is written","calls":[{{call}}],"files":[]}""");

    private static async Task<(List<WorkEvent> Events, FakeChatProvider Worker, FakeChatProvider Reviewer)> Run(params Turn[] plans)
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            [.. plans,
             Turn.Calls1("write_file", """{"path":"a.txt","content":"one"}""", "w1"), Turn.Says("Written."),
             Turn.Calls1("write_file", """{"path":"b.txt","content":"two"}""", "w2"), Turn.Says("Written.")])
            { WhenExhausted = Turn.Says("Done.") };
        var reviewer = new FakeChatProvider(Pass(1), Pass(2));
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            Request);
        return (events, worker, reviewer);
    }

    [Fact]
    public async Task A_line_no_step_is_given_is_asked_about_once_and_the_review_is_shown_what_its_step_was_given()
    {
        var (events, worker, reviewer) = await Run(Turn.Says(Plan("[]")), Turn.Says(Plan("""["O002"]""")));

        var asked = string.Join("\n", worker.Requests[1].Messages.Select(m => m.Content));
        Assert.Contains("Lines of the request no step is given: O002.", asked, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Summary == "The planner gave every line of the request to a step.");
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());

        var second = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.Contains("Lines of the request the plan gives this step", second, StringComparison.Ordinal);
        Assert.Contains("- O002: write b.txt with the word two", second, StringComparison.Ordinal);
        Assert.DoesNotContain("O001: write a.txt", second, StringComparison.Ordinal);                 // the other step's line
    }

    [Fact]
    public async Task What_the_planner_answers_stands_and_a_line_it_still_gives_no_step_is_said()
    {
        var (events, worker, _) = await Run(Turn.Says(Plan("[]")), Turn.Says(Plan("[]")));

        Assert.Contains(events, e => e.Summary == "Given to no step, so judged by no step's review: O002.");
        Assert.Equal(1, worker.Requests.Count(r => r.Messages.Any(m => m.Content?.Contains("no step is given", StringComparison.Ordinal) == true)));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());                             // asked, not refused
    }

    [Fact]
    public async Task A_plan_that_gives_every_line_is_not_asked()
    {
        var (given, worker, _) = await Run(Turn.Says(Plan("""["O002"]""")));
        Assert.DoesNotContain(given, e => e.Summary.Contains("no step is given", StringComparison.Ordinal));
        Assert.DoesNotContain(worker.Requests, r => r.Messages.Any(m => m.Content?.Contains("no step is given", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Only_a_plan_that_assigns_lines_has_lines_given_to_no_step_and_a_shared_line_names_its_other_steps()
    {
        var a = new PlanStep(Guid.NewGuid(), "a", StepStatus.Pending, []) { ObligationIds = ["O001", "O002"] };
        var b = new PlanStep(Guid.NewGuid(), "b", StepStatus.Pending, [a.Id]) { ObligationIds = ["O002"] };
        var request = "one\ntwo\nthree";

        var obligations = RequestObligations.ForPlan(request, new Plan(Guid.NewGuid(), [a, b]));
        Assert.Equal(["O003"], obligations.Unassigned().Select(o => o.Id));
        var owned = obligations.AtStep(1).Owned();
        Assert.Equal(["O001", "O002"], owned.Select(o => o.Unit.Id));
        Assert.Equal(["S2"], owned[1].AlsoTo);
        Assert.Empty(owned[0].AlsoTo);

        var silent = new Plan(Guid.NewGuid(), [a with { ObligationIds = null }, b with { ObligationIds = null }]);
        Assert.Empty(RequestObligations.ForPlan(request, silent).Unassigned());                     // a plan that says nothing
    }
}
