namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Plan Phase 1.4: what a step is handed of the steps before it follows what it declared. An "after" edge is an
/// order and hands on nothing; a step that declares what it consumes is handed those fields' values, and only the
/// names of the rest. Smaller prompts for a local model, and no structured field the step did not ask for. Not an
/// access boundary: files and other context may still show the same things.
/// </summary>
public sealed class TheHandOffFollowsTheDeclarationTests
{
    private const string Heading = "Results handed on by the steps this one depends on";

    private static StepOutput Output(int stepNo, string values)
        => new(stepNo, $"step {stepNo}", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow, values, [], 1, []);

    [Fact]
    public void A_step_that_declares_what_it_consumes_is_handed_those_values_and_the_names_of_the_rest()
    {
        var text = StepOutputContract.ForDependents(
            [Output(1, """{"pages":["wiki/a.md"],"summary":"SECRET-SUMMARY","tags":["x"]}"""), Output(2, """{"count":3}""")],
            [new ConsumeSpec(0, "pages")]);

        Assert.Contains("\"pages\":[\"wiki/a.md\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SUMMARY", text, StringComparison.Ordinal);
        Assert.Contains("'summary'", text, StringComparison.Ordinal);
        Assert.Contains("'tags'", text, StringComparison.Ordinal);
        // A step it consumes nothing of is handed on as before.
        Assert.Contains("""{"count":3}""", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_declaration_everything_is_handed_on_as_before()
    {
        var values = """{"pages":["wiki/a.md"],"summary":"all of it"}""";

        Assert.Equal(StepOutputContract.ForDependents([Output(1, values)]), StepOutputContract.ForDependents([Output(1, values)], null));
        Assert.Contains(values, StepOutputContract.ForDependents([Output(1, values)]), StringComparison.Ordinal);
    }

    // ── through a run ───────────────────────────────────────────────────────────────────

    private static EngineFixture Wiki()
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
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

    [Fact]
    public async Task A_step_that_declares_what_it_consumes_is_handed_only_that()
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
        Assert.DoesNotContain("ONE-PAGE-FOUND", handed, StringComparison.Ordinal);
        Assert.Contains("'summary'", handed, StringComparison.Ordinal);
    }
}
