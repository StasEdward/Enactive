namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// A run judged by something other than its own transcript, without anybody writing a template
/// first.
///
/// <para><b>What was wrong.</b> <c>SuccessEvaluator</c> is the only guard in this engine that
/// looks at the WORKSPACE rather than at text a model wrote — it runs commands and reads exit
/// codes, and an exit code does not confabulate. It was reachable down exactly one path:
/// <c>successCriteria: spec?.SuccessCriteria</c> in both hosts, where <c>spec</c> is a template.
/// Every ad-hoc run fell back to judging the transcript, which is what produced a week of
/// teaching the transcript-judge new vocabulary.</para>
///
/// <para><b>Why a model proposing its own checks is not circular.</b> Three properties, each with
/// a test below. They are proposed BEFORE the work, so they cannot be fitted to the result.
/// <c>SuccessReport.Apply</c> can only make a verdict worse, never better, so a weak check is no
/// worse than the nothing it replaces. And a proposed check that could not be RUN holds nothing
/// back — only one that ran and said no.</para>
/// </summary>
public sealed class ProposedChecksTests
{
    private static WorkContext Context()
        => new(Guid.NewGuid(), "workspace", null, null, null, Array.Empty<string>(), Array.Empty<string>());

    // ── What the planner is asked, and what is taken from its answer ─────────

    /// <summary>A host that does not want proposed checks does not pay for the question.</summary>
    [Fact]
    public void The_question_is_only_asked_when_it_is_wanted()
    {
        Assert.DoesNotContain("checks", Planner.SystemPromptFor(null), StringComparison.Ordinal);
        Assert.Contains("\"checks\"", Planner.SystemPromptFor(null, proposeChecks: true),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The two fields NOT read from the model are the two ways to write a check that cannot fail:
    /// an expected exit code that is not zero, and <c>required: false</c>. A proposed check passes
    /// on 0 and is required, whatever the model wrote.
    /// </summary>
    [Fact]
    public async Task A_model_cannot_propose_a_check_that_cannot_fail()
    {
        var plan = await Plan("""
            {"disposition":"quick_action","title":"t","steps":[],
             "checks":[{"name":"build","command":"dotnet build","expectedExitCode":1,"required":false}]}
            """);

        var check = Assert.Single(plan.Checks);
        Assert.Equal(0, check.ExpectedExitCode);
        Assert.True(check.Required);
        Assert.Equal(CriterionOrigin.Proposed, check.Origin);
    }

    /// <summary>Nothing to prove is a real answer, and the prompt says so twice.</summary>
    [Fact]
    public async Task No_checks_is_a_readable_answer()
    {
        var plan = await Plan("""{"disposition":"quick_action","title":"explain it","steps":[],"checks":[]}""");

        Assert.Empty(plan.Checks);
    }

    /// <summary>
    /// Every check is a real command at the end of the run, with a repair loop behind it when it
    /// fails. A model listing everything it can think of must not turn the verdict into a second
    /// build system.
    /// </summary>
    [Fact]
    public async Task A_plan_may_not_propose_more_than_the_cap()
    {
        var many = string.Join(",", Enumerable.Range(0, 12)
            .Select(i => $$"""{"name":"c{{i}}","command":"cmd{{i}}"}"""));

        var plan = await Plan($$"""{"disposition":"quick_action","title":"t","steps":[],"checks":[{{many}}]}""");

        Assert.Equal(Planner.MaxChecks, plan.Checks.Count);
    }

    /// <summary>A check with no command is not a check; the rest of the plan is still a plan.</summary>
    [Fact]
    public async Task A_malformed_check_is_dropped_and_the_plan_survives()
    {
        var plan = await Plan("""
            {"disposition":"quick_action","title":"t","steps":[],
             "checks":["just a string",{"name":"no command"},{"command":"dotnet build"}]}
            """);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("dotnet build", check.Command);
        Assert.Equal("dotnet build", check.Name);
    }

    // ── What a proposed check is allowed to do to a verdict ──────────────────

    /// <summary>
    /// THE PROPERTY THAT MAKES THIS SAFE TO ARM BY DEFAULT. A check nobody asked for cannot fail a
    /// run by being unrunnable — the policy forbidding it, or the shell not having the command, is
    /// a fact about a guess, not about the work.
    /// </summary>
    [Fact]
    public void A_proposed_check_that_could_not_run_holds_nothing_back()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("build", "dotnet build", Required: true,
                                CriterionOutcome.Unknown, null, "the policy forbids it",
                                CriterionOrigin.Proposed)
        });

        Assert.Empty(report.Blocking);
        Assert.Equal(RunOutcomeKind.Completed, report.Apply(RunOutcomeKind.Completed));
    }

    /// <summary>
    /// And the other half, unchanged: a check a PERSON wrote and that could not be run leaves the
    /// run unfinished. Somebody said this is how you know, and nobody found out.
    /// </summary>
    [Fact]
    public void A_declared_check_that_could_not_run_still_holds_the_run()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("build", "dotnet build", Required: true,
                                CriterionOutcome.Unknown, null, "the policy forbids it")
        });

        Assert.Single(report.Blocking);
        Assert.Equal(RunOutcomeKind.Incomplete, report.Apply(RunOutcomeKind.Completed));
    }

    /// <summary>A proposed check that RAN and said no is evidence, and it does hold the run.</summary>
    [Fact]
    public void A_proposed_check_that_ran_and_failed_fails_the_run()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("build", "dotnet build", Required: true,
                                CriterionOutcome.Failed, 1, "error CS0246",
                                CriterionOrigin.Proposed)
        });

        Assert.Equal(RunOutcomeKind.Failed, report.Apply(RunOutcomeKind.Completed));
    }

    /// <summary>
    /// Checks never promote. A run that failed for something that actually went wrong keeps that
    /// outcome however green the checks are — so the worst a weak proposed check can do is nothing.
    /// </summary>
    [Fact]
    public void Checks_can_only_make_a_verdict_worse()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("build", "dotnet build", Required: true,
                                CriterionOutcome.Passed, 0, null, CriterionOrigin.Proposed)
        });

        Assert.Equal(RunOutcomeKind.Failed, report.Apply(RunOutcomeKind.Failed));
        Assert.Equal(RunOutcomeKind.Incomplete, report.Apply(RunOutcomeKind.Incomplete));
    }

    private static async Task<PlanResult> Plan(string answer)
        => await new Planner().PlanAsync(
            "do the thing", Context(), new FakeChatProvider(Turn.Says(answer)), "m",
            CancellationToken.None, proposeChecks: true);
}
