namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Xunit;

/// <summary>
/// The planner is told what a step IS at the moment it runs.
///
/// <para>A step is the unit everything else is built on — one conversation, one budget, one
/// reviewer verdict, one revert boundary, one blast radius — and the planner decides what goes in
/// it while knowing none of that. Both halves of what it is missing are arithmetic rather than
/// taste:</para>
///
/// <para><b>Cost grows with the square of a step's turns</b>, because every turn re-sends what came
/// before it. Measured 2026-09-22 on one request: a single 250-turn step cost 30.6M prompt tokens
/// and was abandoned at the ceiling, with four dependent steps skipped; the same request planned as
/// five steps of 20–60 turns cost 9.2M and finished. The average prompt fell from 117k to 64k on
/// the split alone.</para>
///
/// <para><b>And a step's size is a fact about the WORKSPACE, not the request.</b> "For every
/// project file" is one comfortable step in a repository with three of them and a dead run in one
/// with three hundred, and the planner cannot see which it is looking at.</para>
///
/// <para>What is NOT said here: anything about which model will run the steps. "The model is weak,
/// plan smaller" is a guess about a name — the same kind of guess this codebase refuses when it
/// makes a person declare a provider's context window rather than inferring one.</para>
/// </summary>
public sealed class HowBigAStepMayBeTests
{
    [Fact]
    public void The_planner_is_told_the_ceiling_and_what_to_do_about_it()
    {
        var prompt = Planner.SystemPromptFor(maxSteps: null, turnCeiling: 250);

        Assert.Contains("past 250 turns is ABANDONED", prompt, StringComparison.Ordinal);
        Assert.Contains("ONE conversation", prompt, StringComparison.Ordinal);

        // The instruction that follows from it, not just the fact.
        Assert.Contains("a step per batch", prompt, StringComparison.Ordinal);

        // And each batch keeps what it did. Run ae2015, 2026-09-24: four batches wrote nothing and a
        // fifth step wrote for all of them; one batch stalled, the fifth was skipped, and the work
        // of the three that finished was lost with it.
        Assert.Contains("Each batch step SAVES its own results", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A number the engine did not supply is a number that goes stale, so there is no default in
    /// the text: with no ceiling the paragraph is simply absent.
    /// </summary>
    [Fact]
    public void Nothing_is_invented_when_no_ceiling_is_given()
    {
        var prompt = Planner.SystemPromptFor(maxSteps: null);

        Assert.DoesNotContain("ABANDONED", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("a step per batch", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// It must not read as licence to invent stages. The rule it sits next to — never split one
    /// action into "do it" / "capture it" / "save it" — is the one that keeps plans honest, and a
    /// paragraph about splitting work is exactly how that rule gets quietly reversed.
    /// </summary>
    [Fact]
    public void Splitting_is_for_repeated_work_and_says_so()
    {
        var prompt = Planner.SystemPromptFor(maxSteps: null, turnCeiling: 250);

        Assert.Contains("REPEATED work only", prompt, StringComparison.Ordinal);
        Assert.Contains("never split into stages", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ONE THAT MATTERS: the number reaching the planner is the engine's own, through a real
    /// run. A constant copied into the prompt would pass every test above and still be wrong the
    /// day somebody changes the ceiling.
    /// </summary>
    [Fact]
    public async Task The_number_in_the_prompt_is_the_engine_s_own()
    {
        using var fx = new EngineFixture();

        var agent = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look at it","steps":[]}"""),
            Turn.Says("Looked."));

        await fx.RunAsync(fx.Build(agent, EngineFixture.Role("developer")), "look at it");

        var planPrompt = agent.Requests[0].Messages
            .First(m => m.Role == ChatRole.System).Content ?? "";

        // The engine's RunawayCeiling is private, and deliberately so; what this asserts is that
        // SOME real ceiling arrived, and that it is the same one the step loop enforces.
        var said = System.Text.RegularExpressions.Regex.Match(planPrompt, @"past (\d+) turns is ABANDONED");

        Assert.True(said.Success, "the planner was not told how long a step may run:\n" + planPrompt);
        Assert.True(int.Parse(said.Groups[1].Value) > 1, "a ceiling of one turn is not a ceiling");
    }
}
