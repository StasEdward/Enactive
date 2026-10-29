namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Intents;
using Enactive.Core.Rules;
using Xunit;

/// <summary>
/// The step review is given the rules a person confirmed (Plan Phase 5.3), as the worker was: told only the request, it
/// passed a step that went against a standing instruction the worker had been given, and a rule nobody checks is one the
/// worker may follow or not. A run with no rules is reviewed exactly as before.
/// </summary>
public sealed class TheStepReviewIsGivenTheRulesTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";
    private const string Rule = "Leave the project file alone: a coverage task changes tests, never the package list.";

    private static async Task<string> ReviewOf(AppliedRules rules)
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"report.md","content":"done"}""", "w1"),
            Turn.Says("Wrote the report."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []) { Rules = rules };
        var engine = fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var _ in engine.SubmitIntentAsync(
                           new Intent(Guid.NewGuid(), "write the report", IntentSource.CommandBar, context, DateTimeOffset.UtcNow), cts.Token)) { }
        return string.Join("\n", Assert.Single(reviewer.Requests).Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task The_review_is_given_each_rule_the_worker_was_and_none_when_there_are_none()
    {
        var rules = AppliedRules.Compose([], [new RuleEntry(Guid.NewGuid(), RuleScope.Workspace, RuleHeading.Deviations, Rule, null,
            DateTimeOffset.UnixEpoch, RuleStatus.Confirmed)]);

        var review = await ReviewOf(rules);
        Assert.Contains("WORKSPACE RULES", review, StringComparison.Ordinal);
        Assert.Contains("- " + Rule, review, StringComparison.Ordinal);
        Assert.Contains("the request wins", review, StringComparison.Ordinal);

        Assert.DoesNotContain("WORKSPACE RULES", await ReviewOf(AppliedRules.None), StringComparison.Ordinal);
    }
}
