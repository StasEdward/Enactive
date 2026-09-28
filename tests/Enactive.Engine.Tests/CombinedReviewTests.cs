namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

public sealed class CombinedReviewTests
{
    [Fact]
    public async Task Missing_semantic_assessment_is_repaired_by_reviewer_before_verdict()
    {
        var good = Verdicts.Combined(Verdicts.Shown("verified", 1), "run");
        var bad = JsonNode.Parse(good.Text!)!;
        bad["assessments"]!.AsObject().Remove("verification");
        var provider = new FakeChatProvider(Turn.Says(bad.ToJsonString()), good);
        var result = await Review(provider);
        Assert.True(result.Pass);
        Assert.Contains("$.assessments.verification", provider.Requests[1].Messages.Last().Content!);
    }

    [Fact]
    public async Task Unknown_assessment_does_not_become_worker_rejection_even_with_top_level_fail()
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("verified", 1), "run").Text!)!;
        answer["verdict"] = "fail";
        answer["assessments"]!["verification"]!["verdict"] = "unknown";
        answer["assessments"]!["verification"]!["reason"] = "Test file not shown";
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var result = await Review(provider);
        Assert.Contains("Test file not shown", result.IncompleteReason!);
        Assert.Null(result.RepairAdvice);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData("proof", ActionOutcome.Succeeded, null)]
    [InlineData("claim", ActionOutcome.Succeeded, 0)]
    [InlineData("requirement", ActionOutcome.Failed, null)]
    [InlineData("requirement", ActionOutcome.Refused, 1)]
    public async Task Invalid_negative_test_references_are_reviewer_contract_errors(
        string location, ActionOutcome outcome, int? exit)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "custom_tool", "{}", outcome, "result", exitCode: exit);
        var bad = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("negative check", 1), "run").Text!)!;
        var path = location switch
        {
            "proof" => "$.proof",
            "claim" => "$.claims[0]",
            _ => "$.claims[0].requirements[0]"
        };
        var target = location switch
        {
            "proof" => bad["proof"]!,
            "claim" => bad["claims"]![0]!,
            _ => bad["claims"]![0]!["requirements"]![0]!
        };
        target["shown"] = "expected-failure";
        var rejected = JsonNode.Parse(Verdicts.Combined(Verdicts.NotShown("Required failure was not observed"), "run").Text!)!;
        rejected["verdict"] = "fail";
        rejected["notes"] = "Required failure was not observed";
        var provider = new FakeChatProvider(Turn.Says(bad.ToJsonString()), Turn.Says(rejected.ToJsonString()));
        var result = await Review(provider, journal.Describe());
        Assert.False(result.Pass); // Repairing the contract need not change the result to pass.
        Assert.Null(result.IncompleteReason);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(path + ".calls[0]", provider.Requests[1].Messages.Last().Content!);
    }

    [Theory]
    [InlineData("proof", true)]
    [InlineData("claim", true)]
    [InlineData("requirement", true)]
    [InlineData("empty", true)]
    [InlineData("proof", false)]
    [InlineData("claim", false)]
    [InlineData("requirement", false)]
    [InlineData("empty", false)]
    public async Task Citation_errors_are_repaired_by_reviewer_without_reexecuting_or_reverting_work(string location, bool corrected)
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write","steps":[]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"keep"}"""), Turn.Says("done"));
        var good = Verdicts.Combined(Verdicts.Shown("file written", 1), "run");
        var bad = JsonNode.Parse(good.Text!)!;
        var target = location switch
        {
            "claim" => bad["claims"]![0]!,
            "requirement" => bad["claims"]![0]!["requirements"]![0]!,
            _ => bad["proof"]!
        };
        target["calls"] = location == "empty" ? new JsonArray() : new JsonArray(999);
        var badTurn = Turn.Says(bad.ToJsonString());
        var reviewer = new FakeChatProvider(badTurn, corrected ? good : badTurn);
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            reviewContent: false, checkSoundness: true, reviewRetries: 2), "Write result.txt");
        Assert.Equal(corrected ? RunOutcomeKind.Completed : RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal("keep", fx.Read("result.txt"));
        Assert.Equal(3, worker.Requests.Count);
        Assert.Equal(2, reviewer.Requests.Count);
        Assert.Contains(".calls", reviewer.Requests[1].Messages.Last().Content!);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.ArtifactReverted or EventKind.ReviewFailed);
    }

    [Fact]
    public async Task A_substantive_rejection_is_returned_without_asking_reviewer_to_change_it()
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.NotShown("Missing mutation check"), "run").Text!)!;
        answer["verdict"] = "fail";
        answer["notes"] = "Missing mutation check";
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var result = await Review(provider);
        Assert.False(result.Pass);
        Assert.Null(result.IncompleteReason);
        Assert.Equal("Missing mutation check", result.Notes);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scope_correction_stays_with_reviewer_and_preserves_worker_output(bool corrected)
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write","steps":[]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"preserve me"}"""), Turn.Says("done"));
        var good = Verdicts.Combined(Verdicts.Shown("file written", 1), "run", "O001", "O002");
        var bad = Verdicts.Combined(Verdicts.Shown("file written", 1), "unknown", "O001", "O002");
        var reviewer = new FakeChatProvider(bad, corrected ? good : bad);
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewContent: false, reviewRetries: 1), "Write result.txt\nKeep its contents");
        Assert.Equal(corrected ? RunOutcomeKind.Completed : RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Equal("preserve me", fx.Read("result.txt"));
        Assert.Equal(3, worker.Requests.Count);
        Assert.Equal(2, reviewer.Requests.Count);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.ArtifactReverted or EventKind.ReviewFailed);
    }

    [Fact]
    public async Task One_clarification_lists_all_scope_errors_with_nested_field_paths()
    {
        var obligations = RequestObligations.Create("Implement\nTest", "test", 2, ["implement", "test"]);
        var good = Verdicts.Combined(Verdicts.Shown("tests passed", 1), "S2", "O001", "O002");
        var bad = JsonNode.Parse(good.Text!)!;
        bad["claims"]![0]!["scope"] = "S1";
        bad["claims"]![0]!["shown"] = "not-by-any-call";
        bad["claims"]![0]!["calls"] = new JsonArray();
        bad["claims"]![1]!["requirements"]![0]!["scope"] = "S1";
        var provider = new FakeChatProvider(Turn.Says(bad.ToJsonString()).Reporting(10, 5), good.Reporting(20, 6));

        var result = await Review(provider, obligations: obligations);

        Assert.True(result.Soundness!.Sound);
        Assert.Equal(2, provider.Requests.Count);
        var correction = provider.Requests[1].Messages.Last().Content!;
        // An explicit non-evidentiary deferral is now normalized; the mixed scope is still invalid.
        Assert.DoesNotContain("$.claims[0].shown", correction);
        Assert.Contains("$.claims[0].scope", correction);
        Assert.Contains("$.claims[1].requirements[0].shown", correction);
        Assert.Contains("$.claims[1].requirements[0].calls", correction);
        Assert.Equal(30, result.PromptTokens);
        Assert.Equal(11, result.CompletionTokens);
    }

    [Fact]
    public async Task Schema_and_coverage_errors_are_collected_even_when_one_claim_cannot_be_parsed()
    {
        var obligations = RequestObligations.Create("Build\nTest\nReport");
        var good = Verdicts.Combined(Verdicts.Shown("verified", 1), "run", "O001", "O002", "O003");
        var bad = JsonNode.Parse(good.Text!)!;
        bad["proof"]!["calls"] = new JsonArray("wrong type");
        bad["claims"]![0]!.AsObject().Remove("requirements");
        bad["claims"]![1]!["id"] = "O001";
        bad["claims"]![1]!["scope"] = "unknown";
        bad["claims"]![2]!["id"] = "O999";
        bad["claims"]![2]!["requirements"]![0]!["global"] = "yes";
        var provider = new FakeChatProvider(Turn.Says(bad.ToJsonString()), good);

        Assert.True((await Review(provider, obligations: obligations)).Soundness!.Sound);
        var correction = provider.Requests[1].Messages.Last().Content!;
        foreach (var path in new[] { "$.proof.calls[0]", "$.claims[0].requirements", "$.claims[1].id",
                     "$.claims[1].scope", "$.claims[2].id", "$.claims[2].requirements[0].global" })
            Assert.Contains(path, correction);
        Assert.Contains("missing obligation ID 'O002'", correction);
        Assert.Contains("missing obligation ID 'O003'", correction);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Unrepaired_structure_is_incomplete_after_one_clarification_with_all_errors_retained()
    {
        var obligations = RequestObligations.Create("Implement\nTest", "test", 2, ["implement", "test"]);
        var bad = Verdicts.Combined(Verdicts.Shown("done", 1), "S1", "O001", "O002");
        var provider = new FakeChatProvider(bad, bad);
        var result = await Review(provider, obligations: obligations);
        Assert.False(result.Pass);
        Assert.Null(result.Soundness);
        Assert.Contains("$.claims[0].shown", result.IncompleteReason!);
        Assert.Contains("$.claims[1].requirements[0].shown", result.IncompleteReason!);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public void Blank_lines_are_preserved_but_do_not_require_independent_claims()
    {
        const string request = "\nBuild\n\n\nTest\n\n";
        var obligations = RequestObligations.Create(request);
        Assert.Equal(2, obligations.Items.Count);
        Assert.Equal(request, string.Concat(obligations.Items.Select(o => o.Text)));
    }

    [Fact]
    public async Task Incomplete_review_keeps_the_workers_files()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write","steps":[]}"""),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"preserve me"}"""), Turn.Says("done"));
        var reviewer = new FakeChatProvider(Turn.Says("invalid"), Turn.Says("invalid"));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewRetries: 0), "Write result.txt");
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal("preserve me", fx.Read("result.txt"));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
    }
    private static EvidenceView Evidence(int count = 1, bool failed = false, int budget = 16000)
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < count; i++) journal.Record(1, "run_command", "build", failed ? ActionOutcome.Failed : ActionOutcome.Succeeded,
            i == 0 ? "FIRST_CALL_EVIDENCE " + new string('x', 400) : "ok " + new string('y', 400));
        return journal.Describe(0, budget);
    }

    private static Task<ReviewResult> Review(FakeChatProvider provider, EvidenceView? evidence = null, RequestObligations? obligations = null,
        Func<int, int, string?>? beforeRetry = null)
        => new Reviewer().ReviewWithProofAsync("build", "done", evidence ?? Evidence(), [], [],
            obligations ?? RequestObligations.Create("Run the exact build command"), provider, "strong", default, beforeRetry: beforeRetry);

    [Fact]
    public async Task One_reply_preserves_independent_truth_proof_and_obligation_checks_and_usage()
    {
        var provider = new FakeChatProvider(Verdicts.Combined(Verdicts.Shown("build output", 1), "run").Reporting(40, 10));
        var result = await Review(provider);
        Assert.True(result.Pass);
        Assert.True(result.Soundness!.Sound);
        Assert.Single(provider.Requests);
        Assert.Equal(40, result.PromptTokens);
        Assert.Equal(10, result.CompletionTokens);
        Assert.Contains("claims", provider.Requests[0].ResponseSchema!);
        Assert.DoesNotContain("maxItems", provider.Requests[0].ResponseSchema!);
    }

    [Fact]
    public async Task More_than_four_evidence_requests_are_rejected_without_expanding_evidence()
    {
        var evidence = Evidence(100, budget: 1200);
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("first check", 1), "run").Text!)!;
        answer["need_evidence"] = new JsonArray(1, 2, 3, 4, 5);
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()), Turn.Says(answer.ToJsonString()));
        var result = await Review(provider, evidence);
        Assert.False(result.Pass);
        Assert.NotNull(result.IncompleteReason);
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(1, evidence.VisibleActionIds);
        Assert.DoesNotContain("FIRST_CALL_EVIDENCE", provider.Requests[1].Messages[1].Content!);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("unproven")]
    [InlineData("wrong-scope")]
    public async Task Successful_command_does_not_replace_complete_obligation_claims(string kind)
    {
        var obligations = RequestObligations.Create("Build the project\nMutation-check EACH new test");
        var json = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("build output", 1), "run", "O001", "O002").Text!)!;
        var claims = json["claims"]!.AsArray();
        if (kind == "missing") claims.RemoveAt(1);
        if (kind == "unknown") claims[1]!["id"] = "O999";
        if (kind == "duplicate") claims[1]!["id"] = "O001";
        if (kind == "unproven")
        {
            claims[1]!["shown"] = "no";
            claims[1]!["what"] = "only one test was mutation-checked";
            claims[1]!["requirements"]![0]!["shown"] = "no";
            claims[1]!["requirements"]![0]!["what"] = "other tests were not mutation-checked";
        }
        if (kind == "wrong-scope") claims[1]!["scope"] = "invented-step";
        var provider = new FakeChatProvider(Turn.Says(json.ToJsonString()));
        var result = await Review(provider, obligations: obligations);
        if (kind != "unproven")
        {
            Assert.False(result.Pass);
            Assert.NotNull(result.IncompleteReason);
            Assert.Equal(2, provider.Requests.Count);
            return;
        }
        Assert.True(result.Pass); // truth verdict remains distinct
        Assert.False(result.Soundness!.Sound);
        Assert.Single(provider.Requests); // do not retry a clear rejection to solicit a pass
    }

    [Fact]
    public async Task Failed_outcomes_are_rejected_even_when_the_model_says_pass()
    {
        var result = await Review(new(Verdicts.Combined(Verdicts.Shown("claimed success", 1), "run")), Evidence(failed: true));
        Assert.False(result.Soundness!.Sound);
    }

    [Fact]
    public async Task Hidden_call_can_only_be_cited_after_addressed_evidence_retrieval()
    {
        var evidence = Evidence(100, budget: 1200);
        Assert.DoesNotContain(1, evidence.VisibleActionIds);
        var combined = Verdicts.Combined(Verdicts.Shown("first check", 1), "run");
        var direct = await Review(new(combined, combined), evidence);
        Assert.Null(direct.Soundness);
        Assert.Contains("was not shown", direct.IncompleteReason!);
        var need = JsonNode.Parse(combined.Text!)!;
        need["need_evidence"] = new JsonArray(1);
        var provider = new FakeChatProvider(Turn.Says(need.ToJsonString()).Reporting(20, 5), combined.Reporting(30, 7));
        var result = await Review(provider, evidence);
        Assert.True(result.Soundness!.Sound);
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain("FIRST_CALL_EVIDENCE", provider.Requests[0].Messages[1].Content!);
        Assert.Contains("Call [1] (Succeeded)", provider.Requests[1].Messages[1].Content!);
        Assert.Contains("FIRST_CALL_EVIDENCE", provider.Requests[1].Messages[1].Content!);
        Assert.Equal(50, result.PromptTokens);
        Assert.Equal(12, result.CompletionTokens);
        Assert.DoesNotContain(1, evidence.VisibleActionIds); // original snapshot stays immutable
    }

    [Fact]
    public async Task Clarification_is_bounded_and_checks_budget_before_spending_again()
    {
        var provider = new FakeChatProvider(Verdicts.Pass().Reporting(20, 10), Verdicts.Combined(Verdicts.Shown("ok", 1), "run"));
        var budget = new RunBudget(new(MaxTokens: 30), DateTimeOffset.UtcNow);
        var result = await Review(provider, beforeRetry: budget.TurnExhaustedAfter);
        Assert.NotNull(result.BudgetExhausted);
        Assert.Single(provider.Requests);
        Assert.Equal(20, result.PromptTokens);
        var broken = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());
        Assert.False((await Review(broken)).Pass);
        Assert.Equal(2, broken.Requests.Count);
    }

    [Fact]
    public async Task Truncated_combined_answer_never_approves_even_if_the_json_looks_complete()
    {
        var good = Verdicts.Combined(Verdicts.Shown("ok", 1), "run");
        var provider = new FakeChatProvider(good with { FinishReason = "length" }, good with { FinishReason = "length" });
        Assert.False((await Review(provider)).Pass);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public void Step_scope_can_defer_only_to_a_real_other_step_and_does_not_claim_completion()
    {
        var obligations = RequestObligations.Create("build\nmutate", "build", 1, ["build", "mutate"]);
        var claims = new[] { new ObligationClaim("O001", "S1", new(ProofClaimKind.Shown, [1], "build succeeded")),
            new ObligationClaim("O002", "S2", new(ProofClaimKind.NotShown, [], "mutation belongs to the next step")) };
        Assert.True(ObligationAudit.Check(obligations, claims, Evidence()).Sound);
        Assert.False(ObligationAudit.Check(obligations, [claims[0], claims[1] with { Scope = "S3" }], Evidence()).Sound);
    }

    [Fact]
    public async Task Planner_worker_and_reviewer_share_lossless_ids_including_late_constraints()
    {
        using var fx = new EngineFixture();
        var request = new string('x', 5000) + "\nUse EXACT_COMMAND for every test.";
        var obligations = RequestObligations.Create(request);
        Assert.Equal(request, string.Concat(obligations.Items.Select(o => o.Text)));
        var worker = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"analyse"}"""), Turn.Says("analysis"));
        var reviewer = new FakeChatProvider(Verdicts.Combined(Verdicts.NotByAnyCall("analysis of both requirements"), "run", "O001", "O002"));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true, reviewRetries: 0), request);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        foreach (var sent in worker.Requests.Concat(reviewer.Requests))
        {
            var text = string.Join("\n", sent.Messages.Select(m => m.Content));
            Assert.Contains("O002", text);
            Assert.Contains("Use EXACT_COMMAND for every test.", text);
        }
    }
}
