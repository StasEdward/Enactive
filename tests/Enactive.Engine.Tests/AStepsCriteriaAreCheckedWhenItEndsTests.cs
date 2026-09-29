namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Run 68f92f, 2026-09-29: the plan attached "file_exists TicTacToe/CoverageReport.md" to its last step; the step
/// found the previous run's report under another name, said nothing needed doing, its reviewer agreed, and the
/// run failed on the missing file five seconds later. A file criterion the plan attached to a step is checked
/// when that step ends, and the step is told once what fails. Deliberately not code: invoice totals and a summary.
/// </summary>
public sealed class AStepsCriteriaAreCheckedWhenItEndsTests
{
    private const string Plan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Add up the invoices","dependsOn":[]},
                  {"title":"Write the summary","dependsOn":[0],
                   "criteria":[{"kind":"file_exists","path":"reports/Summary.md"}]}]}
        """;

    [Fact]
    public async Task A_step_whose_criterion_fails_as_it_ends_is_told_once_and_can_meet_it()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        fx.Write("invoices/a.txt", "total: 10");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Says("The invoices add up to 10."),
            Turn.Calls1("write_file", """{"path":"reports/summary-notes.md","content":"Total: 10"}""", "w1"),
            Turn.Says("Summary written."),
            Turn.Calls1("write_file", """{"path":"reports/Summary.md","content":"Total: 10"}""", "w2"),
            Turn.Says("Summary written where the plan asks."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "add up the invoices and write a summary");

        Assert.Single(events, e => e.Summary.StartsWith("The plan's criteria for this step fail as it ends: reports/Summary.md exists ('reports/Summary.md' does not exist.)", StringComparison.Ordinal));
        var told = worker.Requests.Select(r => r.Messages.Last().Content ?? "").Single(c => c.StartsWith("Before this step ends", StringComparison.Ordinal));
        Assert.Contains("- reports/Summary.md exists: 'reports/Summary.md' does not exist.", told, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("reports/Summary.md exists", StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task A_criterion_of_another_step_is_not_checked_early_and_one_still_failing_reaches_the_reviewer()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Says("The invoices add up to 10."),                                  // step 1: the summary is step 2's, not asked for here
            Turn.Says("Summary written."),
            Turn.Says("I cannot write it."));                                         // told once, still not there
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "add up the invoices and write a summary");

        Assert.Single(events, e => e.Summary.StartsWith("The plan's criteria for this step fail as it ends", StringComparison.Ordinal));
        Assert.DoesNotContain("engine_checked_step_criteria", string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)), StringComparison.Ordinal);
        var second = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.Contains("engine_checked_step_criteria", second, StringComparison.Ordinal);
        Assert.Contains("- reports/Summary.md exists: FAIL - 'reports/Summary.md' does not exist.", second, StringComparison.Ordinal);
    }

    [Fact]
    public void A_criterion_keeps_the_step_it_was_written_in_even_when_repeated_at_the_top()
    {
        using var doc = JsonDocument.Parse("""
            {"steps":[{"title":"a"},{"title":"b","criteria":[{"kind":"file_exists","path":"r.md"}]}],
             "criteria":[{"kind":"file_exists","path":"r.md"},{"kind":"file_exists","path":"whole.md"}]}
            """);
        var read = TypedCriteria.Read(doc.RootElement);

        Assert.Equal(2, read.Count);
        Assert.Equal(1, read.Single(c => c.Path == "r.md").Step);
        Assert.Null(read.Single(c => c.Path == "whole.md").Step);
    }
}
