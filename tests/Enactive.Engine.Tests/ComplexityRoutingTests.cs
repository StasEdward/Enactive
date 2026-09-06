namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// The planner's complexity rating is only a word until something routes on it. These pin the lever
/// that the rubric pulls — after the 2026-09-06 runs, where the rubric said writing a document is
/// never complex and every knowledge-heavy guide went to the cheapest model available.
/// </summary>
public sealed class ComplexityRoutingTests
{
    private const string MixedPlan = """
        {"disposition":"task","title":"two steps",
         "steps":[{"title":"recall external facts","dependsOn":[],"complexity":"complex"},
                  {"title":"tidy the wording","dependsOn":[],"complexity":"trivial"}]}
        """;

    [Fact]
    public async Task A_complex_step_runs_on_the_heavy_model_and_a_trivial_one_on_the_light_model()
    {
        using var fx = new EngineFixture();

        var planner = new FakeChatProvider(Turn.Says(MixedPlan));
        var light = new FakeChatProvider { WhenExhausted = Turn.Says("done cheaply") };
        var heavy = new FakeChatProvider { WhenExhausted = Turn.Says("done properly") };

        var providers = new MapProviderFactory(
            planner,
            (Routers.LightProviderId, light),
            (Routers.HeavyProviderId, heavy));

        var orchestrator = fx.Build(providers, router: Routers.WithComplexityRouting());
        await fx.RunAsync(orchestrator, "write two things");

        Assert.NotEmpty(heavy.Requests);
        Assert.NotEmpty(light.Requests);

        Assert.Contains(
            heavy.Requests,
            r => r.Messages.Any(m => m.Content?.Contains("recall external facts", StringComparison.Ordinal) == true));
        Assert.Contains(
            light.Requests,
            r => r.Messages.Any(m => m.Content?.Contains("tidy the wording", StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData("trivial", StepComplexity.Trivial)]
    [InlineData("normal", StepComplexity.Normal)]
    [InlineData("complex", StepComplexity.Complex)]
    [InlineData("COMPLEX", StepComplexity.Complex)]
    [InlineData("  complex  ", StepComplexity.Complex)]
    public async Task The_planner_reads_the_rating_the_model_sent(string rating, StepComplexity expected)
    {
        var plan = $$"""
            {"disposition":"task","title":"one step",
             "steps":[{"title":"the step","dependsOn":[],"complexity":"{{rating}}"}]}
            """;

        var result = await Plan(plan);

        Assert.Equal(expected, Assert.Single(result.Plan!.Steps).Complexity);
    }

    // An unrecognised rating must land on the standard model, not the expensive one: a typo should
    // not quietly start spending.
    [Theory]
    [InlineData("""{"disposition":"task","title":"t","steps":[{"title":"s","dependsOn":[],"complexity":"enormous"}]}""")]
    [InlineData("""{"disposition":"task","title":"t","steps":[{"title":"s","dependsOn":[]}]}""")]
    public async Task An_unknown_or_missing_rating_falls_back_to_normal(string plan)
    {
        var result = await Plan(plan);

        Assert.Equal(StepComplexity.Normal, Assert.Single(result.Plan!.Steps).Complexity);
    }

    private static Task<PlanResult> Plan(string answer)
    {
        var provider = new FakeChatProvider(Turn.Says(answer));
        var context = new WorkContext(
            Guid.NewGuid(), "workspace", null, null, null,
            Array.Empty<string>(), Array.Empty<string>());

        return new Planner().PlanAsync("do the thing", context, provider, "planner-model", CancellationToken.None);
    }
}
