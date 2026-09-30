namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Xunit;

/// <summary>
/// The reviewer is shown the user's own request, verbatim - not only the step's title, which is the
/// planner's paraphrase of a piece of it.
///
/// <para><b>Measured 2026-09-24, run 4f779e.</b> The request said "Run the tests with THAT command
/// and no other." The step ran 22 different <c>dotnet test --filter …</c> invocations instead, and
/// execution review passed it - the reviewer had nothing to check that instruction against, because
/// the step's title ("Write new tests in existing style") said nothing about which command to run,
/// and nothing else reaching the reviewer did either.</para>
/// </summary>
public sealed class TheReviewerSeesTheRequestTests
{
    private static readonly string[] NoArtifacts = Array.Empty<string>();

    // ── the prompt builders ──────────────────────────────────────────────────

    // ── through a real run ───────────────────────────────────────────────────

    [Fact]
    public async Task An_execution_review_is_shown_the_actual_request()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"run the tests"}"""),
            Turn.Calls1("run_command", """{"command":"echo ran the tests"}""", "c1"),
            Turn.Says("Ran the tests."));
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        await fx.RunAsync(orchestrator, "Run the tests with THAT command and no other.");

        var prompt = reviewer.Requests.Last().Messages.Last().Content ?? "";
        Assert.Contains("Run the tests with THAT command and no other.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_content_review_is_shown_the_actual_request()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a guide"}"""),
            Turn.Calls1("write_file", """{"path":"guide.md","content":"Some text."}"""),
            Turn.Says("Wrote it."));
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        await fx.RunAsync(orchestrator, "Write a guide. Every page must cite its source.");

        var prompt = reviewer.Requests.Last().Messages.Last().Content ?? "";
        Assert.Contains("Every page must cite its source.", prompt, StringComparison.Ordinal);
    }

    /// <summary>Every step of a multi-step plan is shown the SAME request - not one step's title standing in for it.</summary>
    [Fact]
    public async Task Every_step_of_a_plan_is_shown_the_same_request()
    {
        using var fx = new EngineFixture();
        const string plan = """
            {"disposition":"task","title":"Two sets",
             "steps":[{"title":"Alpha does the first half","dependsOn":[]},
                      {"title":"Beta does the second half","dependsOn":[0]}]}
            """;
        var worker = new FakeChatProvider(
            Turn.Says(plan),
            Turn.Says("Alpha done."),
            Turn.Says("Beta done."));
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var orchestrator = fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer);
        await fx.RunAsync(orchestrator, "Use exactly the word 'xylophone' somewhere in every step.");

        Assert.Equal(2, reviewer.Requests.Count);
        Assert.All(reviewer.Requests, r =>
            Assert.Contains("xylophone", r.Messages.Last().Content ?? "", StringComparison.Ordinal));
    }
}
