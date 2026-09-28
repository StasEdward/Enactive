namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Events;
using Xunit;

public sealed class ReviewRepairContractTests
{
    private static JsonNode Answer() => JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("content checked"), "run").Text!)!;
    private static JsonNode Defect()
    {
        var answer = Answer();
        answer["report_checks"] = new JsonArray(new JsonObject {
            ["source_id"] = "worker-report", ["fragment_id"] = "F1", ["kind"] = "inferred",
            ["verdict"] = "fail", ["reason"] = "The count in the summary is wrong",
            ["calls"] = new JsonArray(), ["obligation_ids"] = new JsonArray()
        });
        answer["repairs"] = new JsonArray(Verdicts.Repair("$.report_checks[0]", "Incorrect count",
            "State one record, not two", "worker-report", "F1"));
        return answer;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("pass")]
    [InlineData("wrong-target")]
    [InlineData("no-op")]
    [InlineData("blank-change")]
    [InlineData("unknown-id")]
    public async Task Inconsistent_repair_is_corrected_by_reviewer_without_dispatching_it(string defect)
    {
        var bad = Defect();
        switch (defect)
        {
            case "missing":
                bad["report_checks"]![0]!["reason"] = "This statement is correct; retained for completeness";
                bad["repairs"] = new JsonArray(); break;
            case "pass": bad["report_checks"]![0]!["verdict"] = "pass"; break;
            case "wrong-target": bad["repairs"]![0]!["target"] = "work"; break;
            case "no-op": bad["repairs"]![0]!["change"] = "Two records"; break;
            case "blank-change": bad["repairs"]![0]!["change"] = ""; break;
            case "unknown-id": bad["repairs"]![0]!["obligation_ids"] = new JsonArray("O999"); break;
        }
        var provider = new FakeChatProvider(Turn.Says(bad.ToJsonString()), Turn.Says(Answer().ToJsonString()));
        var result = await new Reviewer().ReviewWithProofAsync("write", "Two records",
            new ExecutionJournal().Describe(), [], [], RequestObligations.Create("Write one record"), provider, "review", default);
        Assert.True(result.Pass);
        Assert.Null(result.RepairAdvice);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("repair", provider.Requests[1].Messages.Last().Content!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void File_repair_renders_real_path_fragment_and_original_requirement_without_hash_ids()
    {
        var source = new ReviewSources("done");
        source.AddFile("nested/checks.cs", "Assert(count > 0);");
        var answer = Answer();
        answer["assessments"]!["verification"]!["verdict"] = "fail";
        answer["repairs"] = new JsonArray(Verdicts.Repair("$.assessments.verification", "Only existence is checked",
            "Compare the complete result", ReviewSources.FileId("nested/checks.cs"), "F1"));
        var obligations = RequestObligations.Create("Verify all returned records.");
        Assert.Empty(ReviewRepairContract.Errors(answer.ToJsonString(), source, obligations));
        var rendered = ReviewRepairContract.Render(answer.ToJsonString(), source, obligations);
        Assert.Contains("nested/checks.cs", rendered);
        Assert.Contains("Assert(count", rendered);
        Assert.Contains("Verify all returned records.", rendered);
        Assert.DoesNotContain(ReviewSources.FileId("nested/checks.cs"), rendered);
    }

    [Fact]
    public async Task Message_only_repair_reaches_worker_as_message_target_without_extra_file_changes()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}"""),
            Turn.Says("Two records"), Turn.Says("One record"));
        var bad = Defect();
        var reviewer = new FakeChatProvider(Turn.Says(bad.ToJsonString()),
            Verdicts.Combined(Verdicts.Shown("One record and accurate message", 1), "run"));
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(),
            reviewProvider: reviewer, checkSoundness: true, reviewContent: false), "Write one record");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal("one", fx.Read("record.txt"));
        Assert.Equal(4, worker.Requests.Count);
        var repair = worker.Requests.Last().Messages.Last().Content!;
        Assert.Contains("Repair contract", repair);
        Assert.Contains("worker-message", repair);
        Assert.Contains("Write one record", repair);
        Assert.Contains("State one record", repair);
        Assert.Single(events, e => e.Kind == EventKind.ToolInvoked);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactReverted);
    }
}
