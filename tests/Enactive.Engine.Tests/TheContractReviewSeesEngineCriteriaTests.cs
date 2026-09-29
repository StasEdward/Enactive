namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Run 68f92f, 2026-09-29: the plan made up "CoverageReport.md" for a report the request named no file for, and
/// the contract review never saw it - criteria the engine decides were set aside before the review, whole. They
/// are shown now, in their typed form, with where each name came from and what is on disk there. The review may
/// correct what the PLAN chose; a name the REQUEST gave stays, and nothing is lost by an answer that forgets one.
/// Deliberately not code: invoices and their summary.
/// </summary>
public sealed class TheContractReviewSeesEngineCriteriaTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("contract").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SuccessCriterionDefinition File(string path, int? step = null)
    {
        var typed = new TypedCriterion(TypedCriterionKind.FileExists, Path: path, NonEmpty: true);
        return new SuccessCriterionDefinition($"{path} exists", $"file_exists {path} (not empty)", 0, Origin: CriterionOrigin.Proposed)
            { Typed = typed, Step = step };
    }

    private static Plan TwoSteps()
    {
        var add = new PlanStep(Guid.NewGuid(), "add up", StepStatus.Pending, []);
        var write = new PlanStep(Guid.NewGuid(), "write summary", StepStatus.Pending, [add.Id])
            { Output = new StepOutputSchema("s2", 1, [new StepOutputField("summary", StepOutputFieldType.Path, "the summary file")]) };
        return new Plan(Guid.NewGuid(), [add, write]);
    }

    private static string Answer(string engineCriteria)
        => """{"sources":[{"id":"O001","assessment":"a summary"}],"checks":[],"forbidden_effects":[],"action_policy":null,"unresolved":null,"engine_criteria":"""
           + engineCriteria + "}";

    private async Task<(PlanResult Result, FakeChatProvider Planner)> Review(string request, string answer, params SuccessCriterionDefinition[] criteria)
    {
        var planner = new FakeChatProvider(Turn.Says(answer));
        var result = await PlanCheckReview.RunAsync(new PlanResult(IntentDisposition.Task, "invoices", TwoSteps()) { Checks = criteria },
            request, new WorkContext(null, "workspace", null, null, null, [], []),
            planner, "strong", new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, workspaceRoot: _root);
        Assert.Null(result.IncompleteReason);
        return (result, planner);
    }

    [Fact]
    public async Task The_review_is_shown_each_criterion_typed_with_where_its_name_came_from_and_what_is_there()
    {
        Directory.CreateDirectory(Path.Combine(_root, "reports"));
        System.IO.File.WriteAllText(Path.Combine(_root, "reports", "invoice-summary.md"), "Total: 10");
        System.IO.File.WriteAllText(Path.Combine(_root, "reports", "notes.md"), "n");

        var (_, planner) = await Review("Add up the invoices, write a summary and list them in totals.csv.", Answer("[]"),
            File("reports/InvoiceSummary.md", step: 1), File("totals.csv"));

        var sent = planner.Requests[0].Messages;
        Assert.Contains("never restate one as a check", sent[0].Content, StringComparison.Ordinal);
        var body = sent[^1].Content!;
        using var doc = JsonDocument.Parse(body[body.IndexOf('{', body.IndexOf("Plan and draft final criteria:", StringComparison.Ordinal))..]);
        var shown = doc.RootElement.GetProperty("engineCriteria").EnumerateArray().ToArray();
        Assert.Equal("E1", shown[0].GetProperty("id").GetString());
        Assert.Equal(EngineCriteriaReview.ChosenByPlan, shown[0].GetProperty("provenance").GetString());
        var there = shown[0].GetProperty("onDiskNow");
        Assert.False(there.GetProperty("exists").GetBoolean());
        Assert.Equal(["invoice-summary.md"], there.GetProperty("similar").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(EngineCriteriaReview.NamedByRequest, shown[1].GetProperty("provenance").GetString());
        Assert.Empty(doc.RootElement.GetProperty("checks").EnumerateArray());   // not among the commands
    }

    [Fact]
    public async Task A_path_the_plan_chose_is_corrected_or_moved_to_the_file_a_step_hands_on_and_that_is_said()
    {
        var (result, _) = await Review("Add up the invoices and write a summary.", Answer("""
            [{"id":"E1","decision":"path_from","path_from":{"step":1,"field":"summary"},"reason":"the request names no file"},
             {"id":"E2","decision":"path","path":"reports/invoice-summary.md","reason":"the existing summary"}]
            """), File("reports/InvoiceSummary.md", step: 1), File("reports/Summary.md"));

        Assert.Equal(["the file step 2 hands on as 'summary' exists", "reports/invoice-summary.md exists"], result.Checks.Select(c => c.Name));
        Assert.Equal(1, result.Checks[0].Step);                                     // everything else as it was
        Assert.Contains(result.ContractNotes, n => n.StartsWith("Moved by the contract review to the file a step hands on", StringComparison.Ordinal));
        Assert.Contains(result.ContractNotes, n => n.StartsWith("Path corrected by the contract review", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_name_the_request_gave_is_kept_whatever_the_review_says()
    {
        var totals = File("totals.csv");
        var (result, _) = await Review("Add up the invoices into totals.csv.", Answer("""
            [{"id":"E1","decision":"drop","reason":"not needed"}]
            """), totals);

        Assert.Equal(totals, Assert.Single(result.Checks));
        Assert.StartsWith("Kept 'totals.csv exists': its file is named by the request", Assert.Single(result.ContractNotes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"id":"E1","decision":"drop"}]""", "was incomplete")]                                               // a drop says why, or it is none
    [InlineData("""[{"id":"E1","decision":"path","path":"../outside.md","reason":"x"}]""", "is not inside the workspace")]
    [InlineData("""[{"id":"E1","decision":"path_from","path_from":{"step":0,"field":"summary"},"reason":"x"}]""", "declares no output field")]
    [InlineData("""[{"id":"E1","decision":"command","reason":"x"}]""", "was incomplete")]
    public async Task A_change_that_cannot_be_made_keeps_the_criterion_and_says_why(string decisions, string why)
    {
        var summary = File("reports/Summary.md");
        var (result, _) = await Review("Add up the invoices and write a summary.", Answer(decisions), summary);

        Assert.Equal(summary, Assert.Single(result.Checks));
        Assert.Contains(why, Assert.Single(result.ContractNotes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_criterion_the_plan_chose_may_be_dropped_with_its_reason_and_one_not_answered_for_is_kept()
    {
        var (result, _) = await Review("Add up the invoices and write a summary.", Answer("""
            [{"id":"E1","decision":"drop","reason":"the summary is the step's answer, not a file"},{"id":"E9","decision":"drop","reason":"x"}]
            """), File("reports/Summary.md"), File("reports/Totals.md"));

        Assert.Equal("reports/Totals.md exists", Assert.Single(result.Checks).Name);
        Assert.Equal("Dropped by the contract review: 'reports/Summary.md exists' - the summary is the step's answer, not a file",
            Assert.Single(result.ContractNotes));
    }

    [Fact]
    public async Task A_locked_review_is_not_asked_about_them_and_changes_none()
    {
        var summary = File("reports/Summary.md");
        var planner = new FakeChatProvider(Turn.Says(Answer("""[{"id":"E1","decision":"drop","reason":"x"}]""")));
        var result = await PlanCheckReview.RunAsync(new PlanResult(IntentDisposition.Task, "invoices", TwoSteps()) { Checks = [summary] },
            "Add up the invoices and write a summary.", new WorkContext(null, "workspace", null, null, null, [], []),
            planner, "strong", new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, preserveCriteria: true, workspaceRoot: _root);

        Assert.Equal(summary, Assert.Single(result.Checks));
        Assert.DoesNotContain("engineCriteria\":[", planner.Requests[0].Messages[^1].Content, StringComparison.Ordinal);
    }
}
