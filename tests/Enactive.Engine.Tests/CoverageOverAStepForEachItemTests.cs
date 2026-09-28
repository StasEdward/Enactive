namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Run 2508838d (and 4f1d97 before it): the planner stated covers_all over a step done for each page, with
/// the page's findings as text - and the criterion was dropped, "declares no results output". A step done
/// for each item gives each item a step of its own, so what that step hands on is the item's result,
/// whatever its field's type; the evidence is recorded for the step's own item.
/// </summary>
public sealed class CoverageOverAStepForEachItemTests
{
    private const string Plan = """
        {"disposition":"task","title":"audit the wiki",
         "steps":[{"title":"list pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"audit page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},
                   "output":{"findings":{"type":"text","description":"what the page gets wrong"}}},
                  {"title":"compile","dependsOn":[1]}],
         "criteria":[{"kind":"covers_all","source":{"step":0,"field":"pages"},"results":{"step":1,"field":"findings"},"evidence":"file_read"}]}
        """;

    [Fact]
    public void A_criterion_over_a_step_for_each_item_is_accepted_whatever_the_field_type()
    {
        using var doc = JsonDocument.Parse(Plan);
        var list = new PlanStep(Guid.NewGuid(), "list pages", StepStatus.Pending, [])
            { Output = new StepOutputSchema("s1", 1, [new StepOutputField("pages", StepOutputFieldType.PathList, "p")]) };
        var audit = new PlanStep(Guid.NewGuid(), "audit page", StepStatus.Pending, [list.Id])
        {
            ForEach = new ForEachSource(0, "pages"),
            Output = new StepOutputSchema("s2", 1, [new StepOutputField("findings", StepOutputFieldType.Text, "f")])
        };
        var plan = new Plan(Guid.NewGuid(), [list, audit, new PlanStep(Guid.NewGuid(), "compile", StepStatus.Pending, [audit.Id])]);
        var (accepted, dropped) = TypedCriteria.Validate(TypedCriteria.Read(doc.RootElement), Path.GetTempPath(), [], plan);
        Assert.Empty(dropped);
        Assert.True(Assert.Single(accepted).Typed!.FromRun);
    }

    /// <summary>
    /// THE ONE THAT MATTERS: page a is read whole and its findings handed on; page b's findings are handed on
    /// without it being read. The criterion holds, and says which page is not covered and why.
    /// </summary>
    [Fact]
    public async Task Each_pages_findings_count_only_with_the_page_read_whole()
    {
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\n");
        fx.Write("wiki/b.md", "# B\n");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s0"), Turn.Says("Listed two."),
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "ra"),
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"nothing wrong"}""", "sa"), Turn.Says("Audited a."),
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"nothing wrong either"}""", "sb"), Turn.Says("Audited b."),
            Turn.Says("Compiled."));

        var events = await fx.RunAsync(fx.Build(worker), "audit every page");

        Assert.DoesNotContain(events, e => e.Summary.Contains("Planner criterion dropped", StringComparison.Ordinal));
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("FAIL", StringComparison.Ordinal)
                                     && e.Summary.Contains("covered 1 of 2", StringComparison.Ordinal)
                                     && e.Summary.Contains("wiki/b.md (never read)", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult
                                     && e.Summary.Contains("not counted as covered: wiki/b.md (never read)", StringComparison.Ordinal));
    }
}
