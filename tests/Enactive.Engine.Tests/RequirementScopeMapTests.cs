namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;

public sealed class RequirementScopeMapTests
{
    private static Plan Plan(bool shared = true) => DagPlan.FromSpecs([
        new("Implement", [], DependenciesDeclared: true) { ObligationIds = ["O001"] },
        new("Verify", [0], DependenciesDeclared: true) { ObligationIds = shared ? ["O001"] : [] }
    ]);

    private static EvidenceView Evidence()
    {
        var journal = new ExecutionJournal();
        journal.Record(2, "run_command", "tests", ActionOutcome.Succeeded, "passed");
        return journal.Describe();
    }

    [Fact]
    public void Missing_assignments_remain_visible_and_each_step_has_its_own_scope()
    {
        var basis = RequestObligations.ForPlan("Implement\nA requirement the planner omitted", Plan());
        var first = basis.AtStep(1);
        var second = basis.AtStep(2);
        Assert.Equal("S1", first.CurrentScope);
        Assert.Equal("S2", second.CurrentScope);
        Assert.Equal(new[] { "S1", "S2" }, second.ScopeMap!["O001"]);
        Assert.Empty(second.ScopeMap["O002"]);
        Assert.Contains("unspecified, NOT waived", second.MappingPrompt());
        Assert.Contains("A requirement the planner omitted", second.Describe());
        var legacy = RequestObligations.ForPlan("Implement", LinearPlan.FromTitles(["one", "two"]));
        Assert.Empty(legacy.ScopeMap!["O001"]);
    }

    [Fact]
    public void Checkpoint_serialization_preserves_assignments_and_accepts_legacy_steps()
    {
        var step = Plan().Steps[0];
        var checkpoint = new CheckpointStep(step.Id, step.Title, step.DependsOn, "Normal", "Done")
            { ObligationIds = step.ObligationIds };
        var roundTrip = JsonSerializer.Deserialize<CheckpointStep>(JsonSerializer.Serialize(checkpoint))!;
        Assert.Equal(step.ObligationIds, roundTrip.ObligationIds);
        var legacy = JsonSerializer.Deserialize<CheckpointStep>("""{"Id":"00000000-0000-0000-0000-000000000001","Title":"old","DependsOn":[],"Complexity":"Normal","Status":"Pending"}""")!;
        Assert.Null(legacy.ObligationIds);
    }

}
