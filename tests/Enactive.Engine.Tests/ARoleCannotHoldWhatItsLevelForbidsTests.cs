namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A role's permission level has to cover every tool it names.
///
/// <para>Raised by an outside review, 2026-09-23: <i>"Reviewer = Observe, run_command = Execute —
/// the test should forbid that combination even if somebody adds it by accident."</i> Nothing did.
/// <c>MailRoles.CanCarry</c> asks this question for <c>send_email</c> alone, because mail was the
/// first tool where getting it wrong was obviously expensive; the general form was never
/// written.</para>
///
/// <para><b>What the gap looks like from a settings screen.</b> The role editor offers every
/// registered tool to every role, so a reviewer can be given <c>run_command</c> with two clicks.
/// The engine then refuses it at call time, correctly — and the model has been told all evening
/// that it has a shell. A capability that is offered and then denied is worse than one that was
/// never offered: it is a plan built on a tool that cannot run.</para>
/// </summary>
public sealed class ARoleCannotHoldWhatItsLevelForbidsTests
{
    private static readonly IToolRegistry Registry = new ToolRegistry(EngineFixture.ShippedTools());

    [Fact]
    public void Every_shipped_role_can_actually_call_everything_it_names()
    {
        var wrong = new List<string>();

        foreach (var worker in DefaultWorkers.Seed(new ModelRef("fake", "fake-model")))
            foreach (var tool in worker.ToolAllowlist)
            {
                if (tool.Contains('*'))
                    continue;

                var needed = Registry.RequiredLevelOf(tool);
                if (needed > worker.DefaultLevel)
                    wrong.Add($"{worker.Id} is {worker.DefaultLevel} and names {tool}, which needs {needed}");
            }

        Assert.True(wrong.Count == 0,
            "A role that names a tool its level forbids has the tool offered to the model and "
            + "refused at call time, which is worse than not offering it: the plan is built on "
            + "something that cannot run. " + string.Join("; ", wrong));
    }

    /// <summary>
    /// And the check has teeth — the combination the review named is caught. Written out because a
    /// test that cannot fail proves nothing, which is the rule this codebase gives its own planner.
    /// </summary>
    [Fact]
    public void The_combination_the_review_named_would_be_caught()
    {
        var reviewer = DefaultWorkers.Seed(new ModelRef("fake", "fake-model"))
                                     .Single(w => w.Id == "reviewer");

        Assert.Equal(PermissionLevel.Observe, reviewer.DefaultLevel);
        Assert.True(Registry.RequiredLevelOf("run_command") > reviewer.DefaultLevel,
                    "run_command must need more than Observe, or the check above means nothing");
    }
}
