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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Verification_of_shared_requirement_is_not_foreign_step_completion(bool shared)
    {
        var obligations = RequestObligations.ForPlan("Implement and verify", Plan(shared)).AtStep(2);
        var proof = new ProofClaim(ProofClaimKind.Shown, [1], "Current tests verify the implementation");
        var claims = new[] { new ObligationClaim("O001", "S1", proof)
            { Requirements = [new("Functional correctness", "S1", false, proof)] } };
        Assert.Equal(shared, ObligationAudit.Check(obligations, claims, Evidence()).Sound);
    }

    [Fact]
    public void Shared_mapping_does_not_turn_missing_evidence_into_success_or_defer_global_constraints()
    {
        var obligations = RequestObligations.ForPlan("Implement and verify", Plan()).AtStep(2);
        var proof = new ProofClaim(ProofClaimKind.NotShown, [], "Tests were not run");
        Assert.False(ObligationAudit.Check(obligations, [new("O001", "S2", proof)], Evidence()).Sound);
        var shown = new ProofClaim(ProofClaimKind.Shown, [1], "passed");
        Assert.False(ObligationAudit.Check(obligations, [new ObligationClaim("O001", "S2", shown)
            { Requirements = [new("Do not modify tests", "S1", true, shown)] }], Evidence()).Sound);
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

    [Fact]
    public async Task Planner_worker_and_reviewer_share_mapping_across_implementation_and_verification()
    {
        using var fx = new EngineFixture { ShortReview = false };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"Implement and verify","steps":[{"title":"Implement","dependsOn":[],"obligations":["O001"]},{"title":"Verify","dependsOn":[0],"obligations":["O001"]}]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"correct"}"""), Turn.Says("Implemented"),
            Turn.Calls1("read_file", """{"path":"result.txt"}"""), Turn.Says("Verified"));
        var reviewer = new FakeChatProvider(
            Verdicts.Combined(Verdicts.Shown("Implemented", 1), "S1"),
            Verdicts.Combined(Verdicts.Shown("Verified earlier implementation", 2), "S1"));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewContent: false, reviewRetries: 0), "Implement and verify result.txt");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(2, reviewer.Requests.Count);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.ReviewFailed or EventKind.ArtifactReverted);
        const string map = "\"O001\":[\"S1\",\"S2\"]";
        Assert.Contains(map, string.Join("\n", worker.Requests.Last().Messages.Select(m => m.Content)));
        Assert.All(reviewer.Requests, r => Assert.Contains(map, string.Join("\n", r.Messages.Select(m => m.Content))));
        Assert.Equal("correct", fx.Read("result.txt"));
    }
}
