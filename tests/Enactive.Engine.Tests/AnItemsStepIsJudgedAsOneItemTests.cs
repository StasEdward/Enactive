namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Run d91b6a45, 2026-09-28: a step for one item was rejected because "the requirement mandates a persisted
/// findings file; none exists" - the document the engine assembles from every item's result, which no item's
/// step may write. Retried, it read that document, got "File not found", and ended INCOMPLETE on it. Deliberately
/// not the wiki: modules, and a dependency report.
/// </summary>
public sealed class AnItemsStepIsJudgedAsOneItemTests
{
    private const string Plan = """
        {"disposition":"task","title":"audit the modules",
         "steps":[{"title":"find modules","dependsOn":[],"output":{"modules":{"type":"path[]","description":"module folders"}}},
                  {"title":"audit module","dependsOn":[0],"forEach":{"step":0,"field":"modules"},"report":"Docs/deps.md",
                   "output":{"findings":{"type":"results","description":"unused dependencies per module"}}}]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_reviewer_is_told_the_document_is_the_engines_and_reading_it_is_answered_not_failed(bool shortReview)
    {
        using var fx = new EngineFixture { StepOutputs = true, DynamicSteps = true, ShortReview = shortReview };
        fx.Write("src/Billing/Billing.csproj", "<Project />");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"modules":["src/Billing"]}""", "s0"), Turn.Says("Found one."),
            Turn.Calls1("read_file", """{"path":"Docs/deps.md"}""", "r1"),                              // the shared document
            Turn.Calls1("read_file", """{"path":"src/Billing/Billing.csproj"}""", "r2"),
            Turn.Calls1(StepOutputContract.ToolName, """{"findings":{"src/Billing":"no unused dependencies"}}""", "s1"),
            Turn.Says("Audited."));
        var reviewer = shortReview ? new FakeChatProvider(Turn.Says("""{"verdict":"pass","reason":"the step did its part","calls":[1],"files":[]}"""), Turn.Says("""{"verdict":"pass","reason":"the step did its part","calls":[2],"files":[]}""")) : new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(
            fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "audit every module and write the unused dependencies into Docs/deps.md");

        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith(
            "read_file -> not run: 'Docs/deps.md' is the document the engine assembles", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("audit module: src/Billing — done", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Summary.Contains("File not found: Docs/deps.md", StringComparison.Ordinal));

        var itemReview = string.Join("\n", reviewer.Requests.Last().Messages.Select(m => m.Content));
        Assert.Contains("What this step is, from the plan: it is done for one item - src/Billing", itemReview, StringComparison.Ordinal);
        Assert.Contains("Docs/deps.md, which the ENGINE assembles", itemReview, StringComparison.Ordinal);
        var firstReview = string.Join("\n", reviewer.Requests.First().Messages.Select(m => m.Content));
        Assert.DoesNotContain("What this step is, from the plan", firstReview, StringComparison.Ordinal);   // not for particular items
    }

    [Fact]
    public void Only_a_step_for_particular_items_has_a_note_and_only_a_declared_document_is_named()
    {
        var find = new PlanStep(Guid.NewGuid(), "find modules", StepStatus.Done, []);
        var withReport = new PlanStep(Guid.NewGuid(), "audit module", StepStatus.Pending, [find.Id])
            { ForEach = new ForEachSource(0, "modules"), Report = "Docs/deps.md", Joins = true };
        var without = withReport with { Id = Guid.NewGuid(), Report = null };
        var item = new PlanStep(Guid.NewGuid(), "audit module: src/A", StepStatus.Pending, [find.Id]) { Items = ["src/A"], ExpandedFrom = withReport.Id };
        var plain = item with { Id = Guid.NewGuid(), ExpandedFrom = without.Id };

        Assert.Null(FanOut.ScopeNote(find, [find, withReport, item]));
        Assert.Contains("Docs/deps.md, which the ENGINE assembles", FanOut.ScopeNote(item, [find, withReport, item]), StringComparison.Ordinal);
        var note = FanOut.ScopeNote(plain, [find, without, plain])!;
        Assert.Contains("done for one item - src/A", note, StringComparison.Ordinal);
        Assert.DoesNotContain("ENGINE assembles", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_is_part_of_the_mapping_the_reviewer_reads_and_not_of_the_final_review()
    {
        var plan = new Plan(Guid.NewGuid(), [new PlanStep(Guid.NewGuid(), "a", StepStatus.Pending, []) { ObligationIds = ["O001"] }]);
        var at = RequestObligations.ForPlan("do it", plan).AtStep(1) with { ScopeNote = "it is done for one item - x" };
        Assert.Contains("What this step is, from the plan: it is done for one item - x", at.MappingPrompt(), StringComparison.Ordinal);
        Assert.DoesNotContain("What this step is", (at with { ScopeNote = null }).MappingPrompt(), StringComparison.Ordinal);
        Assert.DoesNotContain("What this step is", at.ForFinalReview().MappingPrompt(), StringComparison.Ordinal);
    }
}
