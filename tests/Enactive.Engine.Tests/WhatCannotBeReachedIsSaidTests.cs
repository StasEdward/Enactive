namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Run 1549ce, 2026-10-09: asked for 50% test coverage of a project mostly made of window code, a step reached 31.5%, was
/// failed and tried again - and the retry changed the application's own code to make it testable, until it went round in
/// circles; 32 minutes. Benchmark build-error the day before: a review failed a step because a build that had failed
/// BEFORE the work, in code the request said to leave, never succeeded - and the worker renamed that code to get past it.
/// The step review may now say what is left cannot be reached within the request (not tried again), and is told a check
/// the workspace cannot give within the request is not the step's to give.
/// </summary>
public sealed class WhatCannotBeReachedIsSaidTests
{
    private const string Plan = """
        {"disposition":"task","title":"coverage","steps":[{"title":"Write the tests","dependsOn":[]},{"title":"Report","dependsOn":[0]}]}
        """;

    private static Turn Unreachable(string reason) => Turn.Says(Verdicts.ShortVerdict("unreachable", reason, [], []));

    [Fact]
    public async Task A_step_whose_rest_cannot_be_reached_is_said_so_and_not_tried_again()
    {
        using var fx = new EngineFixture();
        var worker = new ByStepChatProvider(Plan)
            .Step("Write the tests", Turn.Calls1("write_file", """{"path":"tests/logic.txt","content":"covered: 31.5%"}""", "w1"),
                Turn.Says("Covered the logic: 31.5%. The rest is window code that cannot be tested here."));
        var reviewer = new FakeChatProvider(Unreachable("31.5% reached; the rest is window code no test here can run"));

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            router: Routers.WithReviewer(), reviewProvider: reviewer), "write tests to 50% coverage");

        var step = Assert.Single(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("Write the tests"));
        Assert.Equal(StepOutcomeKind.Incomplete, step.StepOutcome());
        Assert.StartsWith("not reachable within the request: 31.5% reached", step.OutcomeReason(), StringComparison.Ordinal);
        Assert.Equal(2, worker.RequestsFor("Write the tests").Count);        // its two turns: no retry
        Assert.Single(reviewer.Requests);
        Assert.True(fx.Exists("tests/logic.txt"));                          // what it did stays
        Assert.Contains(events, e => e.Kind == EventKind.ReviewFailed && e.Summary.Contains("NOT REACHABLE within the request", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Fact]
    public void The_review_may_answer_unreachable()
    {
        var input = new StepVerdictInput("request", "step", 1, [], "report", null, [], new Enactive.Core.Execution.ExecutionJournal().Describe());

        var (verdict, errors) = StepVerdictReview.Read("""{"verdict":"unreachable","reason":"the rest needs the window code changed","calls":[],"files":[]}""", input);

        Assert.Empty(errors);
        Assert.Equal("the rest needs the window code changed", Assert.IsType<ReviewVerdict.Unreachable>(verdict).Notes);
    }

    [Fact]
    public void The_review_is_told_both_rules()
    {
        Assert.Contains("unreachable: the step did all that can be done within the request", StepVerdictReview.Instruction, StringComparison.Ordinal);
        Assert.Contains("A check the workspace cannot give without going beyond the request", StepVerdictReview.Instruction, StringComparison.Ordinal);
    }
}
