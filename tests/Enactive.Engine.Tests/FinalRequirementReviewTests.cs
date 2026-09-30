namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Events;
using Enactive.Core.Templates;
using Xunit;

public sealed class FinalRequirementReviewTests
{
    [Theory]
    [InlineData("report", "fail", true, true)]
    [InlineData("verification", "fail", true, true)]
    [InlineData("report", "fail", true, false)]
    [InlineData("implementation", "unknown", false, false)]
    public async Task Final_semantic_defect_is_repaired_by_worker_and_reviewed_again(string area, string verdict, bool repairs, bool corrected)
    {
        using var fx = new EngineFixture { ShortReview = false };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"write and verify","steps":[{"title":"write","dependsOn":[]},{"title":"verify","dependsOn":[0]}]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"before"}"""), Turn.Says("written"),
            Turn.Calls1("read_file", """{"path":"result.txt"}"""), Turn.Says("verified"),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"corrected"}"""), Turn.Says("corrected defect"));
        var bad = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("checked", 1, 2), "run").Text!)!;
        bad["assessments"]![area]!["verdict"] = verdict;
        bad["assessments"]![area]!["reason"] = "Specific defect or missing evidence for " + area;
        if (verdict == "fail")
            bad["repairs"] = new JsonArray(Verdicts.Repair("$.assessments." + area, "Specific defect for " + area));
        var reviewer = new FakeChatProvider(Turn.Says(Deferred().ToJsonString()),
            Verdicts.Combined(Verdicts.Shown("verified", 2), "S2"), Turn.Says(bad.ToJsonString()),
            corrected ? Verdicts.Combined(Verdicts.Shown("corrected whole result", 3), "run") : Turn.Says(bad.ToJsonString()));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            reviewContent: false, checkSoundness: true, reviewRetries: 1,
            successCriteria: [new("check", "echo verified")]), "Write and verify result.txt");
        Assert.Equal(corrected ? RunOutcomeKind.Completed : RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal(repairs ? "corrected" : "before", fx.Read("result.txt"));
        Assert.Equal(repairs ? 7 : 5, worker.Requests.Count);
        Assert.Equal(repairs ? 4 : 3, reviewer.Requests.Count);
        Assert.Equal(repairs ? 2 : 1, events.Count(e => e.IsCheck()));
        if (repairs) Assert.Contains("Specific defect", worker.Requests[5].Messages.Last().Content!);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactReverted);
    }

    private static JsonNode Deferred()
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("written", 1), "S1").Text!)!;
        var parts = answer["claims"]![0]!["requirements"]!.AsArray();
        var later = parts[0]!.DeepClone();
        later["scope"] = "S2";
        later["shown"] = "no";
        later["calls"] = new JsonArray();
        later["what"] = "Verification deferred";
        parts.Add(later);
        return answer;
    }

    [Theory]
    [InlineData(1, "pass")]
    [InlineData(2, "pass")]
    [InlineData(1, "defer")]
    [InlineData(2, "reject")]
    [InlineData(1, "malformed")]
    public async Task Deferred_parts_must_be_reconciled_after_all_steps(int parallelism, string final)
    {
        using var fx = new EngineFixture { ShortReview = false };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"write and verify","steps":[{"title":"write","dependsOn":[]},{"title":"verify","dependsOn":[0]}]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"keep"}"""), Turn.Says("written"),
            Turn.Calls1("read_file", """{"path":"result.txt"}"""), Turn.Says("verified"));
        var last = final switch {
            "pass" => Verdicts.Combined(Verdicts.Shown("whole request", 1, 2), "run"),
            "defer" => Verdicts.Combined(Verdicts.NotShown("still deferred"), "S1"),
            "reject" => Turn.Says(JsonNode.Parse(Verdicts.Combined(Verdicts.NotShown("missing requirement"), "run").Text!)!
                .AsObject().AlsoFail().ToJsonString()),
            _ => Turn.Says("invalid")
        };
        var reviewer = new FakeChatProvider(Turn.Says(Deferred().ToJsonString()),
            Verdicts.Combined(Verdicts.Shown("verified", parallelism == 1 ? 2 : 1), "S2"),
            last.Reporting(7, 3), last.Reporting(5, 2));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            reviewContent: false, checkSoundness: true, reviewRetries: 2, maxParallelSteps: parallelism),
            "Write and verify result.txt");
        Assert.Equal(final == "pass" ? RunOutcomeKind.Completed : RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal(5, worker.Requests.Count);
        Assert.Equal("keep", fx.Read("result.txt"));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactReverted);
        var prompt = reviewer.Requests[2].Messages[1].Content!;
        Assert.Contains("FINAL RUN REVIEW", prompt);
        Assert.Contains("step-deferral rules above no longer apply", reviewer.Requests[2].Messages[0].Content!);
        Assert.Contains("write_file", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("result.txt", prompt);
        Assert.Contains("keep", prompt);
        Assert.Equal(final is "defer" or "malformed" ? 4 : 3, reviewer.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregate_is_derived_without_clarification_but_unmet_current_part_still_fails(bool unmet)
    {
        var answer = Deferred();
        var claim = answer["claims"]![0]!;
        claim["shown"] = "no";
        claim["calls"] = new JsonArray();
        if (unmet) claim["requirements"]![0]!["shown"] = "no";
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var journal = new ExecutionJournal();
        journal.Record(1, "write_file", "result.txt", ActionOutcome.Succeeded, "written");
        var result = await new Reviewer().ReviewWithProofAsync("write", "done", journal.Describe(), [], [],
            RequestObligations.Create("write then verify", step: 1, steps: ["write", "verify"]),
            provider, "model", default);
        Assert.Equal(!unmet, result.Soundness!.Sound);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Non_evidentiary_foreign_scope_is_a_deferral_not_a_second_model_call()
    {
        var answer = Deferred();
        answer["claims"]![0]!["requirements"]![1]!["shown"] = "not-by-any-call";
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var journal = new ExecutionJournal();
        journal.Record(1, "write_file", "x", ActionOutcome.Succeeded, "written");
        var result = await new Reviewer().ReviewWithProofAsync("write", "done", journal.Describe(), [], [],
            RequestObligations.Create("write then verify", step: 1, steps: ["write", "verify"]),
            provider, "model", default);
        Assert.True(result.Soundness!.Sound);
        Assert.Equal(ProofClaimKind.NotShown, result.Obligations![0].Requirements![1].Proof.Kind);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Final_review_obeys_remaining_budget_and_preserves_completed_files()
    {
        using var fx = new EngineFixture { ShortReview = false };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"write and verify","steps":[{"title":"write","dependsOn":[]},{"title":"verify","dependsOn":[0]}]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"keep"}"""), Turn.Says("written"),
            Turn.Calls1("read_file", """{"path":"result.txt"}"""), Turn.Says("verified"));
        var reviewer = new FakeChatProvider(Turn.Says(Deferred().ToJsonString()),
            Verdicts.Combined(Verdicts.Shown("verified", 2), "S2").Reporting(100, 0));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            reviewContent: false, checkSoundness: true, limits: new ExecutionLimits(MaxTokens: 100)),
            "Write and verify result.txt");
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal(2, reviewer.Requests.Count);
        Assert.Equal("keep", fx.Read("result.txt"));
        Assert.Contains("Final requirement reconciliation", events.Text());
    }
}

internal static class FinalReviewTestJson
{
    internal static JsonObject AlsoFail(this JsonObject node) { node["verdict"] = "fail"; node["notes"] = "Required verification missing"; return node; }
}
