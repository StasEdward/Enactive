namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Xunit;

public sealed class SemanticReviewAuditTests
{
    private static JsonNode Answer() => JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("Inspected content"), "run").Text!)!;
    private static JsonNode Verification(JsonNode answer) => answer["claims"]![0]!["requirements"]![0]!["verification"]!;

    [Fact]
    public void Green_verification_requires_visible_assertions_and_detection_explanation()
    {
        var answer = Answer();
        Verification(answer)["verdict"] = "pass";
        var errors = SemanticReviewAudit.Errors(answer.ToJsonString(), new ExecutionJournal().Describe(),
            new ReviewSources("done"), RequestObligations.Create("Test the behavior"));
        Assert.Contains(errors, e => e.Contains("assertion references"));
    }

    [Fact]
    public void Existing_assertions_read_through_tools_can_be_cited_without_a_file_write()
    {
        var sources = new ReviewSources("done");
        sources.AddEvidence("read_file existing-checks.py\nassert actual == expected");
        var answer = Answer();
        var check = Verification(answer);
        check["verdict"] = "pass";
        check["detects"] = "Wrong result";
        check["assertions"] = new JsonArray(new JsonObject {
            ["source_id"] = "execution-evidence", ["fragment_id"] = "F2" });
        Assert.Empty(SemanticReviewAudit.Errors(answer.ToJsonString(), new ExecutionJournal().Describe(),
            sources, RequestObligations.Create("Verify behavior")));
    }

    [Theory]
    [InlineData("missing", "F1")]
    [InlineData("worker-report", "F1")]
    [InlineData("file", "F999")]
    public void Invented_or_summary_assertion_references_are_reviewer_errors(string id, string fragment)
    {
        var sources = new ReviewSources("tests pass");
        sources.AddFile("checks.cs", "Assert.Equal(expected, actual);");
        var answer = Answer();
        var check = Verification(answer);
        check["verdict"] = "pass";
        check["detects"] = "Incorrect result";
        check["assertions"] = new JsonArray(new JsonObject {
            ["source_id"] = id == "file" ? ReviewSources.FileId("checks.cs") : id, ["fragment_id"] = fragment });
        Assert.NotEmpty(SemanticReviewAudit.Errors(answer.ToJsonString(), new ExecutionJournal().Describe(),
            sources, RequestObligations.Create("Test result")));
    }

    [Theory]
    [InlineData("fail", false)]
    [InlineData("unknown", true)]
    public async Task Concrete_gap_goes_to_worker_but_missing_evidence_does_not(string verdict, bool incomplete)
    {
        var answer = Answer();
        Verification(answer)["verdict"] = verdict;
        Verification(answer)["reason"] = incomplete ? "Assertion source unavailable" : "Only one collection element compared; compare the complete input";
        if (!incomplete)
            answer["repairs"] = new JsonArray(Verdicts.Repair("$.claims[0].requirements[0].verification",
                "Only one element compared", "Compare the complete input"));
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var result = await new Reviewer().ReviewWithProofAsync("verify", "done", new ExecutionJournal().Describe(), [], [],
            RequestObligations.Create("Verify preservation"), provider, "review", default);
        Assert.False(result.Pass);
        Assert.Equal(incomplete, result.IncompleteReason is not null);
        Assert.Equal(!incomplete, result.RepairAdvice is not null);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public void Visible_requirement_mappings_cannot_be_omitted()
    {
        var sources = new ReviewSources("O001: preserves input");
        var errors = SemanticReviewAudit.Errors(Answer().ToJsonString(), new ExecutionJournal().Describe(), sources,
            RequestObligations.Create("Preserve input"));
        Assert.Contains(errors, e => e.Contains("requirement-map") && e.Contains("worker-report/F1"));
    }

    [Fact]
    public async Task Report_false_observation_overrides_general_pass()
    {
        var answer = Answer();
        answer["report_checks"] = new JsonArray(new JsonObject {
            ["source_id"] = "worker-report", ["fragment_id"] = "F1", ["kind"] = "observed",
            ["verdict"] = "fail", ["reason"] = "Report invents exact output; log contains only a failure label. Remove or label inference.",
            ["calls"] = new JsonArray(1), ["obligation_ids"] = new JsonArray()
        });
        answer["repairs"] = new JsonArray(Verdicts.Repair("$.report_checks[0]",
            "Report invents exact output", "Remove or qualify the unobserved value", "worker-report", "F1"));
        var evidence = new ExecutionJournal();
        evidence.Record(1, "run_command", "check", ActionOutcome.Failed, "FAIL: ordering", exitCode: 1);
        var provider = new FakeChatProvider(Turn.Says(answer.ToJsonString()));
        var result = await new Reviewer().ReviewWithProofAsync("report", "The command printed [3,2,1]",
            evidence.Describe(), [], [], RequestObligations.Create("Report observed outputs"), provider, "review", default);
        Assert.False(result.Pass);
        Assert.Contains("invents exact output", result.RepairAdvice!);
        Assert.Null(result.IncompleteReason);
        Assert.Single(provider.Requests);
    }
}
