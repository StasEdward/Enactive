namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;

public sealed class PlanValidationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Disconnected_cycle_prevents_even_independent_writes(int parallel)
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(Turn.Says("""
            {"disposition":"task","title":"invalid",
             "steps":[{"title":"write","dependsOn":[]},
                      {"title":"A","dependsOn":[2]}, {"title":"B","dependsOn":[1]}]}
            """), Turn.Calls1("write_file", """{"path":"must-not-exist.txt","content":"wrong"}"""));
        var events = await fx.RunAsync(fx.Build(provider, maxParallelSteps: parallel), "write and verify");
        Assert.Equal(2, provider.Requests.Count); // Planning and one structural repair; no worker call.
        Assert.False(File.Exists(fx.PathOf("must-not-exist.txt")));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
        Assert.Equal(3, events.Count(e => e.Kind == EventKind.StepCompleted && e.StepOutcome() == StepOutcomeKind.Skipped));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    [Fact]
    public void Planner_self_dependency_is_preserved_and_rejected()
    {
        var plan = DagPlan.FromSpecs([new("self", [0], DependenciesDeclared: true)]);
        Assert.Equal(plan.Steps[0].Id, Assert.Single(plan.Steps[0].DependsOn));
        Assert.Contains("cycle", PlanValidation.Error(plan));
    }

    [Fact]
    public void Forward_edges_duplicates_diamond_and_disconnected_roots_are_valid()
    {
        var plan = DagPlan.FromSpecs([
            new("join", [1, 2], DependenciesDeclared: true), new("left", [3, 3]),
            new("right", [3]), new("root", []), new("independent", [])]);
        Assert.Null(PlanValidation.Error(plan));
        Assert.Null(PlanValidation.Error(new(Guid.NewGuid(), [])));
    }

    [Fact]
    public void Long_chain_does_not_use_recursive_traversal()
    {
        Assert.Null(PlanValidation.Error(LinearPlan.FromTitles(Enumerable.Repeat("step", 20000))));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task Invalid_checkpoint_is_rejected_before_any_new_work(string defect)
    {
        using var fx = new EngineFixture();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        CheckpointStep[] steps = [
            new(first, "done", [], "Normal", "Done", "Succeeded"),
            new(defect == "duplicate" ? first : second, "pending",
                defect == "cycle" ? [second] : defect == "missing" ? [Guid.NewGuid()] : [], "Normal", "Pending")];
        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "continue", "invalid", null, null, steps, [], [], [], 1, 0);
        var provider = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"bad.txt","content":"wrong"}"""));
        var events = await fx.ResumeAsync(fx.Build(provider), checkpoint);
        Assert.Empty(provider.Requests);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains(defect));
        Assert.Equal(StepOutcomeKind.Succeeded, events.First(e => e.Kind == EventKind.StepCompleted).StepOutcome());
        Assert.Equal(StepOutcomeKind.Skipped, events.Last(e => e.Kind == EventKind.StepCompleted).StepOutcome());
    }
}
