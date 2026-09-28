namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run dd7ca94b, 2026-09-28: the planner was told about "report" and planned a "compile" step instead; the
/// first page step created the report; a page step that handed its findings on as it was told was failed
/// for an edit that never ran. A document made from items is declared before anything runs, and handing
/// on the result settles what the step did not do instead.
/// </summary>
public sealed class ADocumentFromItemsIsDeclaredTests
{
    // ── what handing on settles ────────────────────────────────────────────────────────

    [Fact]
    public void Handing_the_result_on_settles_calls_that_never_ran_and_not_calls_that_failed()
    {
        ToolDefinition[] tools = [new("edit_file", "e", "{}", WorkspaceEffect.Changed, ["path"], RepairsFileFailures: true),
            new("run_command", "c", "{}", Kind: ToolKind.Command)];
        var open = new OpenFailures(tools);
        open.Failed(new ToolCall("c1", "edit_file", """{"path":"Docs/R.md","old_string":"a","new_string":"b"}"""), "cut; nothing executed", didNotRun: true);
        Assert.Equal(1, open.Count);
        open.HandedOn();
        Assert.Equal(0, open.Count);

        var ran = new OpenFailures(tools);
        ran.Failed(new ToolCall("c2", "run_command", """{"command":"dotnet build"}"""), "Command exited with code 1.");
        ran.HandedOn();
        Assert.Equal(1, ran.Count);                                                // what ran and failed stays
    }

    // ── the plan says where the document is ────────────────────────────────────────────

    private static (Plan Plan, IReadOnlyList<PlannedCriterion> Criteria) Shape(bool report, bool after = true)
    {
        var list = new PlanStep(Guid.NewGuid(), "list", StepStatus.Pending, []);
        var each = new PlanStep(Guid.NewGuid(), "check page", StepStatus.Pending, [list.Id])
            { ForEach = new ForEachSource(0, "pages"), Report = report ? "Docs/R.md" : null };
        PlanStep[] steps = after ? [list, each, new(Guid.NewGuid(), "compile", StepStatus.Pending, [each.Id])] : [list, each];
        using var doc = JsonDocument.Parse("""{"criteria":[{"kind":"file_exists","path":"Docs/R.md"}]}""");
        return (new Plan(Guid.NewGuid(), steps), TypedCriteria.Read(doc.RootElement));
    }

    [Fact]
    public void A_document_made_from_items_and_named_by_the_criteria_must_be_declared()
    {
        var (plan, criteria) = Shape(report: false);
        var missing = FanOut.MissingReport(plan, criteria);
        Assert.Equal(1, missing!.Value.Step);
        // A question, not an answer: no path is proposed as the report.
        Assert.Contains("\"report\":\"<path>\"", missing.Value.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("\"report\":\"Docs/R.md\"", missing.Value.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("return the plan unchanged", missing.Value.Diagnostic, StringComparison.Ordinal);

        var (declared, c2) = Shape(report: true);
        Assert.Null(FanOut.MissingReport(declared, c2));
        var (alone, c3) = Shape(report: false, after: false);
        Assert.Null(FanOut.MissingReport(alone, c3));                             // nothing builds on the items
    }

    // ── through a run ──────────────────────────────────────────────────────────────────

    private const string Undeclared = """
        {"disposition":"task","title":"check the wiki",
         "steps":[{"title":"list pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"check page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},
                   "output":{"findings":{"type":"text","description":"what the page gets wrong"}}},
                  {"title":"compile the report","dependsOn":[1]}],
         "criteria":[{"kind":"file_exists","path":"Docs/R.md"}]}
        """;

    /// <summary>
    /// The planner leaves the report out, is asked, and leaves it out again: the engine does NOT pick one. The
    /// page step still cannot write the file the criteria name; the step after the items writes it.
    /// </summary>
    [Fact]
    public async Task A_report_the_planner_does_not_declare_is_not_declared_by_the_engine()
    {
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\n");
        var worker = new FakeChatProvider(
            Turn.Says(Undeclared),
            Turn.Says(Undeclared),                                                                          // asked, and unchanged
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"]}""", "s0"), Turn.Says("Listed."),
            Turn.Calls1("write_file", """{"path":"Docs/R.md","content":"# page a"}""", "w1"),            // refused: the run's result
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"accurate"}""", "s1"), Turn.Says("Checked."),
            Turn.Calls1("write_file", """{"path":"Docs/R.md","content":"# compiled\npage a: accurate\n"}""", "w2"),
            Turn.Says("Compiled."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check every page");

        Assert.Contains(events, e => e.Summary.Contains("The plan declares no report", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Summary.Contains("the engine declares", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith("write_file -> refused: 'Docs/R.md' is what the whole run delivers", StringComparison.Ordinal));
        Assert.StartsWith("# compiled", fx.Read("Docs/R.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The reproduction: find modules, fix each, build - the criteria name src/Program.cs. The engine proposes
    /// nothing, and a report declared on a source file is refused whoever declares it: it would be overwritten.
    /// </summary>
    [Fact]
    public async Task A_source_file_the_criteria_name_is_never_made_a_report()
    {
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true, DynamicSteps = true };
        fx.Write("src/Program.cs", "class Program { }\n");
        fx.Write("src/Mod/A.cs", "class A { }\n");
        const string plan = """
            {"disposition":"task","title":"fix the modules",
             "steps":[{"title":"find modules","dependsOn":[],"output":{"modules":{"type":"path[]","description":"module folders"}}},
                      {"title":"fix module","dependsOn":[0],"forEach":{"step":0,"field":"modules"}},
                      {"title":"build","dependsOn":[1]}],
             "criteria":[{"kind":"file_exists","path":"src/Program.cs"}]}
            """;
        var declaredOnSource = plan.Replace("\"forEach\":{\"step\":0,\"field\":\"modules\"}",
            "\"forEach\":{\"step\":0,\"field\":\"modules\"},\"report\":\"src/Program.cs\"");
        Assert.NotEqual(plan, declaredOnSource);
        var worker = new FakeChatProvider(
            Turn.Says(plan),
            Turn.Says(declaredOnSource),                                                                   // the planner, asked, declares the wrong thing
            Turn.Calls1(StepOutputContract.ToolName, """{"modules":["src/Mod"]}""", "s0"), Turn.Says("Found one."),
            Turn.Says("Fixed it."),
            Turn.Says("Built."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "fix every module");

        Assert.DoesNotContain(events, e => e.Summary.Contains("\"report\":\"src/Program.cs\"", StringComparison.Ordinal)
                                           && e.Kind == EventKind.ContextAssembled && e.Summary.Contains("Asking", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Summary.Contains("will not assemble 'src/Program.cs'", StringComparison.Ordinal));
        Assert.Equal("class Program { }\n", fx.Read("src/Program.cs").Replace("\r", ""));
    }

    [Fact]
    public async Task A_report_the_planner_declares_when_asked_is_used()
    {
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\n");
        var declared = Undeclared.Replace("\"forEach\":{\"step\":0,\"field\":\"pages\"},", "\"forEach\":{\"step\":0,\"field\":\"pages\"},\"report\":\"Docs/R.md\",");
        var worker = new FakeChatProvider(
            Turn.Says(Undeclared), Turn.Says(declared),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"]}""", "s0"), Turn.Says("Listed."),
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":"accurate"}""", "s1"), Turn.Says("Checked."),
            Turn.Calls1(StepOutputContract.ToolName, """{"summary":"One page, accurate."}""", "s2"), Turn.Says("Compiled."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check every page");

        Assert.Contains(events, e => e.Summary.Contains("The planner declared the report.", StringComparison.Ordinal));
        Assert.Contains("One page, accurate.", fx.Read("Docs/R.md"), StringComparison.Ordinal);
    }
}
