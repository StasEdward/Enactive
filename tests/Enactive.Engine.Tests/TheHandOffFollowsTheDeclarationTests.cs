namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Plan Phase 1.4: what a step is handed of the steps before it follows what it declared, when the run holds steps to
/// their declarations (step outputs and typed criteria on). An "after" edge is an order and hands on nothing; of a step
/// it consumes a list from, the lists it does not consume are named and withheld, and every other value is handed on
/// as before. Not an access boundary: files and other context may still show the same things.
/// </summary>
public sealed class TheHandOffFollowsTheDeclarationTests
{
    private const string Heading = "Results handed on by the steps this one depends on";

    private static StepOutput Output(int stepNo, string values)
        => new(stepNo, $"step {stepNo}", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow, values, [], 1, []);

    private static StepOutputSchema Out(params (string Name, StepOutputFieldType Type)[] fields)
        => new("s", 1, fields.Select(f => new StepOutputField(f.Name, f.Type, f.Name)).ToArray());

    private static readonly StepOutputSchema Found = Out(("pages", StepOutputFieldType.PathList), ("tags", StepOutputFieldType.StringList),
        ("summary", StepOutputFieldType.Text));

    private const string FoundValues = """{"pages":["wiki/a.md"],"tags":["SECRET-TAG"],"summary":"ONE-PAGE-FOUND"}""";

    /// <summary>
    /// A consume says which lists the step owes coverage for. The lists it does not consume are withheld, by name; the
    /// rest of what the step handed on is not - a consume can only name a list, so a text field withheld would be one
    /// no declaration could ever reach.
    /// </summary>
    [Fact]
    public void A_step_that_consumes_a_list_is_not_handed_the_lists_it_does_not_consume_and_is_handed_everything_else()
    {
        var text = StepOutputContract.ForDependents(
            [(Output(1, FoundValues), Found), (Output(2, """{"count":3}"""), Out(("count", StepOutputFieldType.Integer)))],
            [new ConsumeSpec(0, "pages")]);

        Assert.Contains("\"pages\":[\"wiki/a.md\"]", text, StringComparison.Ordinal);
        Assert.Contains("ONE-PAGE-FOUND", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-TAG", text, StringComparison.Ordinal);
        Assert.Contains("'tags'", text, StringComparison.Ordinal);
        // A step it consumes nothing of is handed on as before.
        Assert.Contains("""{"count":3}""", text, StringComparison.Ordinal);
    }

    /// <summary>A consume of a field that is no list of that step - a typo, a text field - withholds nothing.</summary>
    [Theory]
    [InlineData("summary")]
    [InlineData("pagez")]
    public void A_consume_that_names_no_list_of_its_source_withholds_nothing(string field)
    {
        var text = StepOutputContract.ForDependents([(Output(1, FoundValues), Found)], [new ConsumeSpec(0, field)]);

        Assert.Contains(FoundValues, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_declaration_everything_is_handed_on_as_before()
    {
        Assert.Equal(StepOutputContract.ForDependents([Output(1, FoundValues)]),
            StepOutputContract.ForDependents([(Output(1, FoundValues), Found)], null));
        Assert.Contains(FoundValues, StepOutputContract.ForDependents([Output(1, FoundValues)]), StringComparison.Ordinal);
    }

    // ── through a run ───────────────────────────────────────────────────────────────────

    private static EngineFixture Wiki(bool typedCriteria = true, bool dynamicSteps = false)
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = typedCriteria, DynamicSteps = dynamicSteps };
        fx.Write("wiki/a.md", "# A\n");
        return fx;
    }

    private static ChatRequest StepRequest(FakeChatProvider worker, string title)
        => worker.Requests.First(r => r.Messages.Any(m => m.Content?.Contains($"Proceed with this step of the plan: {title}", StringComparison.Ordinal) == true));

    private static string? HandOff(ChatRequest request)
        => request.Messages.LastOrDefault(m => m.Content?.StartsWith(Heading, StringComparison.Ordinal) == true)?.Content;

    private const string Waiting = """
        {"disposition":"task","title":"tidy after finding",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"tidy up","dependsOn":[],"after":[0]}]}
        """;

    /// <summary>"after" is an order: the step waits, and is handed none of the data.</summary>
    [Fact]
    public async Task A_step_that_only_waits_for_another_is_handed_none_of_its_results()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(Waiting),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"]}""", "s0"), Turn.Says("Found one."),
            Turn.Says("Tidied."));

        var events = await fx.RunAsync(fx.Build(worker), "find the pages, then tidy up");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Null(HandOff(StepRequest(worker, "tidy up")));
    }

    private const string Consuming = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"},
                                                                  "summary":{"type":"text","description":"what was found"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages"}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """;

    /// <summary>What a step consumes is handed on, and so is what it could not have declared: the source's text.</summary>
    [Fact]
    public async Task A_step_that_consumes_a_list_is_handed_it_and_the_sources_other_values()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(Consuming),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"],"summary":"ONE-PAGE-FOUND"}""", "s0"), Turn.Says("Found one."),
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "r1"),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/a.md":"fine"}}""", "s1"), Turn.Says("Reviewed."));

        var events = await fx.RunAsync(fx.Build(worker), "review the wiki");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var handed = HandOff(StepRequest(worker, "review pages"));
        Assert.NotNull(handed);
        Assert.Contains("\"pages\":[\"wiki/a.md\"]", handed, StringComparison.Ordinal);
        Assert.Contains("ONE-PAGE-FOUND", handed, StringComparison.Ordinal);
    }

    private const string Declaring = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"},
                                                                  "tags":{"type":"string[]","description":"the tags"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages"}]},
                  {"title":"tidy up","dependsOn":[],"after":[0]}]}
        """;

    /// <summary>
    /// Regression guard: the planner is told about consumes and after whenever step outputs are on, but only typed
    /// criteria check them. With typed criteria off, a plan that declares them is handed on exactly as before.
    /// </summary>
    [Fact]
    public async Task With_typed_criteria_off_a_plan_that_declares_consumes_and_after_is_handed_on_as_before()
    {
        using var fx = Wiki(typedCriteria: false);
        var worker = new FakeChatProvider(
            Turn.Says(Declaring),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"],"tags":["SECRET-TAG"]}""", "s0"), Turn.Says("Found one."),
            Turn.Says("Reviewed."), Turn.Says("Tidied."));

        var events = await fx.RunAsync(fx.Build(worker), "review the wiki");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        const string all = """{"pages":["wiki/a.md"],"tags":["SECRET-TAG"]}""";
        Assert.Contains(all, HandOff(StepRequest(worker, "review pages")), StringComparison.Ordinal);
        Assert.Contains(all, HandOff(StepRequest(worker, "tidy up")), StringComparison.Ordinal);
    }

    private const string ForEachTagging = """
        {"disposition":"task","title":"tag the wiki",
         "steps":[{"title":"list pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"},
                                                                  "tags":{"type":"string[]","description":"the tags"}}},
                  {"title":"tag page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},"consumes":[{"step":0,"field":"tags"}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """;

    /// <summary>A step done for each item of a list consumes that list: declaring another consume never withholds it.</summary>
    [Fact]
    public async Task A_step_for_each_item_keeps_its_own_list_when_it_declares_another_consume()
    {
        using var fx = Wiki(dynamicSteps: true);
        var worker = new FakeChatProvider(
            Turn.Says(ForEachTagging),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"],"tags":["draft"]}""", "s0"), Turn.Says("Listed one."),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/a.md":"tagged"}}""", "sa"), Turn.Says("Tagged a."));

        await fx.RunAsync(fx.Build(worker), "tag every page");

        var handed = HandOff(StepRequest(worker, "tag page"));
        Assert.NotNull(handed);
        Assert.Contains("\"pages\":[\"wiki/a.md\"]", handed, StringComparison.Ordinal);
        Assert.Contains("\"tags\":[\"draft\"]", handed, StringComparison.Ordinal);
    }
}
