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

    [Fact]
    public void An_execution_prompt_quotes_the_request_when_given()
    {
        var prompt = Reviewer.BuildExecutionUserPrompt(
            "Write new tests in existing style", "Wrote 4 tests.", "-> dotnet test\n<- exit code 0",
            NoArtifacts, request: "Run the tests with THAT command and no other.");

        Assert.Contains("ORIGINAL REQUEST", prompt, StringComparison.Ordinal);
        Assert.Contains("Run the tests with THAT command and no other.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_content_prompt_quotes_the_request_when_given()
    {
        var files = new[] { new WrittenFile("guide.md", "Install the package.", 21) };

        var prompt = Reviewer.BuildContentUserPrompt(
            "Write the guide", "Wrote it.", files, request: "Every page must cite its source.");

        Assert.Contains("Every page must cite its source.", prompt, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. Nothing is invented when the caller has no request text.</summary>
    [Fact]
    public void Nothing_is_added_when_no_request_is_given()
    {
        var prompt = Reviewer.BuildExecutionUserPrompt(
            "a step", "did it", "-> run_command\n<- exit code 0", NoArtifacts);

        Assert.DoesNotContain("ORIGINAL REQUEST", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_request_keeps_requirements_after_the_old_cutoff()
    {
        var huge = new string('x', 10_000) + "\nEvery new test must be checked by mutation.";

        var prompt = Reviewer.BuildExecutionUserPrompt(
            "a step", "did it", "-> run_command\n<- exit code 0", NoArtifacts, request: huge);

        Assert.Contains("Every new test must be checked by mutation.", prompt, StringComparison.Ordinal);
        Assert.Contains("O002", prompt);
        Assert.DoesNotContain("cut here", prompt, StringComparison.Ordinal);
    }

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
