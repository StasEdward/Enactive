namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

public sealed class PlanningCoverageBudgetTests
{
    private const string Plan = """{"disposition":"quick_action","title":"analyse"}""";

    [Fact]
    public async Task Planner_budget_applies_to_initial_and_format_repair_calls()
    {
        using var fx = new EngineFixture();
        fx.GenerationBudgetsOverride = new(Planner: 777);
        var provider = new FakeChatProvider(Turn.Says("broken"), Turn.Says(Plan), Turn.Says("done"));
        var events = await fx.RunAsync(fx.Build(provider), "analyse");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.All(provider.Requests.Take(2), r =>
        {
            Assert.Equal(GenerationPurpose.Planning, r.Purpose);
            Assert.Equal(777, r.OutputTokenLimit);
            Assert.Null(r.ResponseSchema);
        });
    }

    [Fact]
    public async Task Planner_clarification_checks_pending_usage_without_losing_it()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says("broken").Reporting(30, 10), Turn.Says(Plan));
        var events = await fx.RunAsync(fx.Build(provider, limits: new(MaxTokens: 40)), "analyse");
        Assert.Single(provider.Requests);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
        Assert.Contains(events, e => e.Summary.Contains("having used 40"));
    }

    [Fact]
    public async Task Clarification_context_failure_retains_first_reply_usage()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider { Window = 65536 };
        provider.Answering = _ =>
        {
            provider.Window = 128;
            return Turn.Says("broken").Reporting(30, 10);
        };
        var result = await new Planner().PlanAsync("analyse",
            new(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []), provider, "model", default);
        Assert.Single(provider.Requests);
        Assert.NotNull(result.IncompleteReason);
        Assert.Equal(30, result.PromptTokens);
        Assert.Equal(10, result.CompletionTokens);
    }

    [Fact]
    public async Task Planner_budget_includes_reasoning_allowance()
    {
        using var fx = new EngineFixture();
        fx.GenerationBudgetsOverride = new(Planner: 777);
        var provider = new FakeChatProvider(Turn.Says(Plan), Turn.Says("done")) { ReasoningTokens = 1024 };
        await fx.RunAsync(fx.Build(provider), "analyse");
        Assert.Equal(1801, provider.Requests[0].OutputTokenLimit);
    }

    [Fact]
    public async Task Exhausted_budget_does_not_start_planning()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(Plan));
        var events = await fx.RunAsync(fx.Build(provider, limits: new(MaxTokens: 0)), "analyse");
        Assert.Empty(provider.Requests);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Fact]
    public async Task Truncated_planner_answers_never_execute_even_with_parseable_json()
    {
        using var fx = new EngineFixture();
        var truncated = Turn.Says(Plan) with { FinishReason = "length" };
        var provider = new FakeChatProvider(truncated, truncated, Turn.Says("never"));
        var events = await fx.RunAsync(fx.Build(provider), "analyse");
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
    }

    [Fact]
    public async Task Insufficient_planner_context_is_incomplete_without_calling_provider()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says(Plan)) { Window = 128 };
        var events = await fx.RunAsync(fx.Build(provider), "analyse");
        Assert.Empty(provider.Requests);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    private static EvidenceView Evidence()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", "build", ActionOutcome.Succeeded, "build succeeded");
        return journal.Describe();
    }

}
