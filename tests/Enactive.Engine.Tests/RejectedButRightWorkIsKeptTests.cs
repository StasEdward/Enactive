namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A step the review rejects keeps its files when the review found the work itself right and
/// failed the step for something else - its report, its process.
///
/// <para><b>Measured 2026-09-28, run 3fe4f8.</b> The final review of step 1 said of the seven tests
/// it wrote "implementation: pass ... 7/7 pass", and failed the step for how its commands were run
/// and what its report left out. The step was rejected and the test file put back: the one thing the
/// review had found right was the one thing thrown away.</para>
/// </summary>
public sealed class RejectedButRightWorkIsKeptTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write"}""";

    /// <summary>A review that fails the worker's REPORT, repaired by correcting the reply - the work is not in question.</summary>
    private static JsonNode ReportIsWrong()
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.NotByAnyCall("content checked"), "run").Text!)!;
        answer["report_checks"] = new JsonArray(new JsonObject {
            ["source_id"] = "worker-report", ["fragment_id"] = "F1", ["kind"] = "inferred",
            ["verdict"] = "fail", ["reason"] = "The count in the summary is wrong",
            ["calls"] = new JsonArray(), ["obligation_ids"] = new JsonArray()
        });
        answer["repairs"] = new JsonArray(Verdicts.Repair("$.report_checks[0]", "Incorrect count",
            "State one record, not two", "worker-report", "F1"));
        return answer;
    }

    private static async Task<(IReadOnlyList<WorkEvent> Events, EngineFixture Fx)> Run(JsonNode verdict)
    {
        var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}"""),
            Turn.Says("Two records")) { WhenExhausted = Turn.Says("Two records") };
        var reviewer = new FakeChatProvider { WhenExhausted = Turn.Says(verdict.ToJsonString()) };
        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(),
            reviewProvider: reviewer, checkSoundness: true, reviewContent: false), "Write one record");
        return (events, fx);
    }

    /// <summary>THE ONE THAT MATTERS: rejected for its report, the step's file is still there, and the run says why.</summary>
    [Fact]
    public async Task A_step_rejected_for_its_report_keeps_the_work_the_review_found_right()
    {
        var (events, fx) = await Run(ReportIsWrong());
        using var _ = fx;

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());      // still rejected
        Assert.Equal("one", fx.Read("record.txt"));                        // and not thrown away
        Assert.Contains(events, e => e.Summary.Contains("NOT put back: record.txt", StringComparison.Ordinal)
                                     && e.Summary.Contains("implementation: pass", StringComparison.Ordinal));
    }

    /// <summary>A review that finds the IMPLEMENTATION wrong still has it put back, as it always did.</summary>
    [Fact]
    public async Task A_step_whose_work_the_review_found_wrong_is_put_back()
    {
        var verdict = ReportIsWrong();
        verdict["assessments"]!["implementation"]!["verdict"] = "fail";
        verdict["assessments"]!["implementation"]!["reason"] = "The record is wrong";
        verdict["repairs"]!.AsArray().Add(Verdicts.Repair("$.assessments.implementation", "Wrong record", "Write the right record"));

        var (events, fx) = await Run(verdict);
        using var _ = fx;

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.False(File.Exists(Path.Combine(fx.Root, "record.txt")));
        Assert.Contains(events, e => e.Summary.StartsWith("Rejected work put back: record.txt", StringComparison.Ordinal));
    }

    /// <summary>
    /// Passing the implementation while asking for a change to a saved file is not "the work is
    /// right": the file is named as needing correction, so it is put back.
    /// </summary>
    [Fact]
    public void A_repair_aimed_at_a_saved_file_means_the_file_is_not_kept()
        => Assert.False(Enactive.Agents.ReviewRepairContract.WorkStands(
            SavedFileRepair(), SavedFileSources()));

    private static string SavedFileRepair()
    {
        var answer = ReportIsWrong();
        answer["repairs"] = new JsonArray(Verdicts.Repair("$.report_checks[0]", "Incorrect count", "one record",
            Enactive.Agents.ReviewSources.FileId("record.txt"), "F1"));
        return answer.ToJsonString();
    }

    private static Enactive.Agents.ReviewSources SavedFileSources()
    {
        var sources = new Enactive.Agents.ReviewSources("Two records");
        sources.AddFile("record.txt", "one");
        return sources;
    }
}
