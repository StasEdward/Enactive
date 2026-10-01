namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// How many steps a plan may have, and who decides.
///
/// <para>The planner used to carry "Use 2-4 steps max" in its system prompt. Nothing in the defect log
/// records an incident that earned the number, and it contradicted the product: every built-in
/// template declares a step budget of 6 to 12, so none of them could ever reach its own limit. It
/// was also the quantity Orchestrator.StallLimit describes having already got wrong once - "Work
/// is not the thing to limit; a project with a hundred files needs a hundred turns and no setting
/// should have to say so."</para>
///
/// <para>The budget belongs to the RUN, not to the prompt, and it is now told to the planner
/// instead of guessed at.</para>
/// </summary>
public sealed class PlannerStepBudgetTests
{
    private static WorkContext Context()
        => new(Guid.NewGuid(), "workspace", null, null, null, Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void A_run_with_no_budget_is_told_no_number()
    {
        var prompt = Planner.SystemPromptFor(null);

        Assert.DoesNotContain("at most", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(prompt, Planner.SystemPromptFor(0));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void A_run_with_a_budget_is_told_that_budget(int cap)
    {
        Assert.Contains($"at most {cap} step(s)", Planner.SystemPromptFor(cap), StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard against the number coming back. A cap written into the prompt can only be right
    /// for one template, and there are seven of them declaring six different budgets.
    /// </summary>
    [Fact]
    public void No_step_count_is_written_into_the_prompt_itself()
    {
        var prompt = Planner.SystemPromptFor(null);

        Assert.DoesNotContain("2-4", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("steps max", prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the cap was really for is still said, because it is the part that was doing the work:
    /// a stage that only names part of one action is not a step.
    /// </summary>
    [Fact]
    public void The_rule_against_ceremonial_stages_is_kept()
    {
        var prompt = Planner.SystemPromptFor(null);

        Assert.Contains("NEVER split one command", prompt, StringComparison.Ordinal);
        Assert.Contains("succeed or fail on its own", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the plan shape the cap used to prevent is now actually reachable — nothing downstream
    /// trims it. Seven steps, a real dependency graph, and two of them able to start at once.
    /// </summary>
    [Fact]
    public async Task A_plan_longer_than_four_steps_survives()
    {
        const string sevenSteps = """
            {"disposition":"task","title":"Migrate the store","steps":[
              {"title":"read the schema","dependsOn":[],"complexity":"normal"},
              {"title":"read the callers","dependsOn":[],"complexity":"normal"},
              {"title":"design the migration","dependsOn":[0,1],"complexity":"complex"},
              {"title":"write the migration","dependsOn":[2],"complexity":"normal"},
              {"title":"update the callers","dependsOn":[2],"complexity":"normal"},
              {"title":"run the tests","dependsOn":[3,4],"complexity":"trivial"},
              {"title":"write the notes","dependsOn":[5],"complexity":"normal"}]}
            """;

        var plan = await new Planner().PlanAsync(
            "migrate the store", Context(), new FakeChatProvider(Turn.Says(sevenSteps)),
            "planner-model", CancellationToken.None);

        Assert.Equal(IntentDisposition.Task, plan.Disposition);
        Assert.Equal(7, plan.Plan!.Steps.Count);

        // The graph survived too: the first two steps depend on nothing and can run together.
        Assert.Equal(2, plan.Plan.Steps.Count(s => s.DependsOn.Count == 0));
    }

    /// <summary>
    /// The wiring, end to end: a template that allows eight steps says so to the planner. Asserted
    /// on what the provider was actually SENT, because a budget that is threaded to within one
    /// call of the prompt and not put in it is the shape of bug this repository keeps finding.
    /// </summary>
    [Fact]
    public async Task A_templates_budget_reaches_the_planner()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do it"}"""),
            Turn.Says("Done."));

        await fx.RunAsync(
            fx.Build(provider, limits: new ExecutionLimits(MaxSteps: 8)), "do it");

        var planning = provider.Requests[0];
        Assert.Contains(
            planning.Messages,
            m => m.Content is { } c && c.Contains("at most 8 step(s)", StringComparison.Ordinal));
    }
}
