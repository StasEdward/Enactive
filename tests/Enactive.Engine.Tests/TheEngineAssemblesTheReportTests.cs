namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// The document a step done for each item declares as its report is assembled by code from the
/// engine's records - statuses, counts, table, one section per item - and the model's part is the
/// summary it hands on as a value.
/// </summary>
public sealed class TheEngineAssemblesTheReportTests
{
    private static StepOutput Output(object values) => new(3, "s", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow,
        JsonSerializer.Serialize(values), [], 1, []);

    private static StepRecord Record(StepOutcomeKind outcome, OutcomeCause cause, object? values, string? why = null, bool reviewed = true)
        => new(outcome, cause, why, StepRecord.StandingOf(outcome, cause, values is not null, reviewed), values is null ? null : Output(values));

    [Fact]
    public void Every_status_is_the_engines_and_every_less_than_confirmed_result_says_so()
    {
        var document = ReportDocument.Render("Check the wiki",
        [
            new(["Docs/wiki/Sidebar.md"], Record(StepOutcomeKind.Succeeded, OutcomeCause.None, new { notes = new Dictionary<string, string> { ["Docs/wiki/Sidebar.md"] = "all links resolve" } })),
            new(["Docs/wiki/README.md"], Record(StepOutcomeKind.Incomplete, OutcomeCause.StepIncomplete, null, "unresolved tool call: run_command findstr")),
            new(["Docs/wiki/Settings.md"], Record(StepOutcomeKind.Incomplete, OutcomeCause.StepIncomplete, new { notes = new Dictionary<string, string> { ["Docs/wiki/Settings.md"] = "3 discrepancies" } }, "unresolved tool call")),
            new(["Docs/wiki/Templates.md"], Record(StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUnprocessable, new { notes = new Dictionary<string, string> { ["Docs/wiki/Templates.md"] = "0 discrepancies" } }, "Combined review response has structural errors")),
            new(["Docs/wiki/Console.md"], Record(StepOutcomeKind.ReviewRejected, OutcomeCause.ReviewRejected, new { notes = new Dictionary<string, string> { ["Docs/wiki/Console.md"] = "wrong" } }, "review not passed: the timeout is 100 s"))
        ],
        [new("summarise", "Проверено 5 страниц.", Record(StepOutcomeKind.Succeeded, OutcomeCause.None, new { summary = "x" }))]);

        Assert.Contains("## Summary\n\nПроверено 5 страниц.", document.Replace("\r", ""), StringComparison.Ordinal);
        Assert.Contains("5 item(s): 1 confirmed, 2 not finished, 1 unconfirmed: the review could not be processed, 1 rejected by review.", document, StringComparison.Ordinal);
        Assert.Contains("| Docs/wiki/Sidebar.md | Confirmed | — |", document, StringComparison.Ordinal);
        Assert.Contains("| Docs/wiki/README.md | Not finished | Result not provided; unresolved tool call: run_command findstr |", document, StringComparison.Ordinal);
        Assert.Contains("| Docs/wiki/Templates.md | Unconfirmed: the review could not be processed | Combined review response has structural errors |", document, StringComparison.Ordinal);
        Assert.Contains("> Provisional: handed on before the step broke off, not a finished result.\n\n**notes**\n\n- `Docs/wiki/Settings.md`: 3 discrepancies",
            document.Replace("\r", ""), StringComparison.Ordinal);
        Assert.Contains("> Rejected: the review found this result wrong.", document, StringComparison.Ordinal);
        Assert.Contains("### Docs/wiki/README.md\n\nStatus: Not finished\n\n_Result not provided._", document.Replace("\r", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_after_the_items_of_a_report_hands_on_a_summary()
    {
        var find = new PlanStep(Guid.NewGuid(), "find", StepStatus.Pending, [])
            { Output = new StepOutputSchema("s1", 1, [new StepOutputField("pages", StepOutputFieldType.PathList, "p")]) };
        var each = new PlanStep(Guid.NewGuid(), "review", StepStatus.Pending, [find.Id]) { ForEach = new ForEachSource(0, "pages"), Report = "Docs/R.md" };
        var after = new PlanStep(Guid.NewGuid(), "summarise", StepStatus.Pending, [each.Id]);

        var (plan, _) = FanOut.Validate(new Plan(Guid.NewGuid(), [find, each, after]));

        Assert.Equal(StepOutputFieldType.Text, Assert.Single(plan.Steps[2].Output!.Fields, f => f.Name == "summary").Type);
    }

    /// <summary>
    /// Benchmark scenario wiki-drift, 2026-09-30: the planner declared the report and nothing its items hand on. Told the
    /// document is the engine's and to hand their findings on, then that they hand nothing on, the items put what they
    /// found in their closing messages only; the document read "Result not provided" over a run called Completed. Where
    /// none is declared, the items of a report hand on their findings as text; a declared output is left as it is.
    /// </summary>
    [Fact]
    public void The_items_of_a_report_hand_on_their_findings_when_the_planner_declared_nothing()
    {
        var find = new PlanStep(Guid.NewGuid(), "find", StepStatus.Pending, [])
            { Output = new StepOutputSchema("s1", 1, [new StepOutputField("pages", StepOutputFieldType.PathList, "p")]) };
        var each = new PlanStep(Guid.NewGuid(), "check page", StepStatus.Pending, [find.Id]) { ForEach = new ForEachSource(0, "pages"), Report = "Docs/DRIFT.md" };
        var declared = each with { Id = Guid.NewGuid(), Output = new StepOutputSchema("s2", 1, [new StepOutputField("notes", StepOutputFieldType.Results, "n")]) };
        var noReport = each with { Id = Guid.NewGuid(), Report = null };

        var field = Assert.Single(FanOut.Validate(new Plan(Guid.NewGuid(), [find, each])).Plan.Steps[1].Output!.Fields);
        Assert.Equal((ReportDocument.FindingsField, StepOutputFieldType.Text, true), (field.Name, field.Type, field.Required));
        Assert.Equal("notes", Assert.Single(FanOut.Validate(new Plan(Guid.NewGuid(), [find, declared])).Plan.Steps[1].Output!.Fields).Name);
        Assert.Null(FanOut.Validate(new Plan(Guid.NewGuid(), [find, noReport])).Plan.Steps[1].Output);
    }

    [Fact]
    public async Task What_an_item_found_reaches_the_report_when_its_planner_declared_no_output()
    {
        using var fx = new EngineFixture { StepOutputs = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "Timeout: 60 s\n");
        var worker = new FakeChatProvider(
            Turn.Says("""
                {"disposition":"task","title":"check the wiki",
                 "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                          {"title":"check page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},"report":"Docs/DRIFT.md"}]}
                """),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"]}""", "s0"), Turn.Says("Found one."),
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "ra"),
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"The page says 60 s; the code says 30."}""", "sa"),
            Turn.Says("Checked a.")) { WhenExhausted = Turn.Says("Done.") };

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check the wiki against the code");

        Assert.Contains("The page says 60 s; the code says 30.", fx.Read("Docs/DRIFT.md"), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Summary.Contains("this step hands nothing on as values", StringComparison.Ordinal));
    }

    // ── through a run ──────────────────────────────────────────────────────────────────

    private const string Plan = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},"report":"Docs/REPORT.md",
                   "output":{"notes":{"type":"results","description":"a note per page"}}},
                  {"title":"summarise","dependsOn":[1]}]}
        """;

    /// <summary>
    /// THE ONE THAT MATTERS: one page hands its result on, one never does; the summary is handed on as a
    /// value; the document is written by the engine - statuses, the missing result said as missing, the
    /// summary in its place - and a step that tries to edit it is refused.
    /// </summary>
    [Fact]
    public async Task The_report_is_written_by_the_engine_from_the_records_and_the_summary()
    {
        using var fx = new EngineFixture { StepOutputs = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\n");
        fx.Write("wiki/b.md", "# B\n");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s0"), Turn.Says("Found two."),
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "ra"),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/a.md":"accurate"}}""", "sa"), Turn.Says("Reviewed a."),
            Turn.Says("I looked at b."), Turn.Says("Really."),                                            // b never hands its result on
            Turn.Calls1("write_file", """{"path":"Docs/REPORT.md","content":"all good"}""", "w1"),        // refused: the engine's
            Turn.Calls1(StepOutputContract.ToolName, """{"summary":"Одна страница проверена, одна не завершена."}""", "ss"),
            Turn.Says("Summarised."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "review every page");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith("write_file -> refused: 'Docs/REPORT.md' is written by the engine", StringComparison.Ordinal));
        var report = fx.Read("Docs/REPORT.md").Replace("\r", "");
        Assert.StartsWith("# review page\n", report, StringComparison.Ordinal);
        Assert.Contains("Одна страница проверена, одна не завершена.", report, StringComparison.Ordinal);
        Assert.Contains("| wiki/a.md | Done, not reviewed | — |", report, StringComparison.Ordinal);
        Assert.Contains("| wiki/b.md | Not finished | Result not provided;", report, StringComparison.Ordinal);
        Assert.Contains("- `wiki/a.md`: accurate", report, StringComparison.Ordinal);
        Assert.DoesNotContain("all good", report, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.ArtifactProduced && e.Summary.EndsWith("Docs/REPORT.md", StringComparison.Ordinal));
    }
}
