namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// The shell rule, tested where it is actually enforced.
///
/// <para><c>REMOTE_DESIGN.md</c> §5.4 calls "a task started from the web never runs a shell" the
/// single most important line in the document, and it is the reason a second factor is a should
/// rather than a must. <see cref="RemoteDecisionHandler"/> has refused shells since stage 5 and
/// there are tests for it - and the rule was still broken in production, because a decision
/// handler only ever sees what the POLICY decided to ask about. At the Autonomous tier the policy
/// asks about nothing, the handler was never called, and a task started from a phone ran
/// <c>run_powershell</c>.</para>
///
/// <para>So these ask the permission engine, not the handler. Denying and asking are different
/// answers, and only one of them is a rule: a denied tool never reaches anybody's judgement.</para>
/// </summary>
public sealed class RemotePolicyTests
{
    private static readonly PermissionEngine Engine = new();

    /// <summary>What the engine answers for a tool, at the level the shells actually declare.</summary>
    private static PermissionDecision Decide(string tool, PermissionPolicy policy)
        => Engine.Evaluate(policy, tool, PermissionLevel.Execute);

    /// <summary>The tiers as the application builds them, Observe through Autonomous.</summary>
    private static PermissionPolicy Tier(int level) => level switch
    {
        0 => new PermissionPolicy(PermissionLevel.Observe, ["*"], []),
        1 => new PermissionPolicy(PermissionLevel.Suggest, ["*"], []),
        2 => new PermissionPolicy(PermissionLevel.Execute, ["*"], ["run_command", "run_powershell", "git", "docker"]),
        _ => new PermissionPolicy(PermissionLevel.Autonomous, ["*"], [])
    };

    /// <summary>
    /// The decisive one, and the tier that was actually broken.
    ///
    /// <para>At Autonomous the workspace's own policy allows a shell outright - which is what that
    /// tier means for work started at the machine, and was never a decision anybody made about the
    /// network. A remote run gets it denied.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_remote_run_may_not_run_a_shell_at_any_tier(int level)
    {
        var policy = RemotePolicy.ForRemoteRun(Tier(level));

        Assert.Equal(PermissionDecision.Deny, Decide("run_powershell", policy));
        Assert.Equal(PermissionDecision.Deny, Decide("run_command", policy));
        Assert.Equal(PermissionDecision.Deny, Decide("git", policy));
        Assert.Equal(PermissionDecision.Deny, Decide("docker", policy));
    }

    /// <summary>
    /// Denied, not asked - which is the whole distinction this fix turns on. An ASKED shell reaches
    /// a decision handler, and whether it is refused then depends on which handler is installed and
    /// on nobody having wired one up differently. A DENIED shell never reaches anyone.
    /// </summary>
    [Fact]
    public void The_answer_is_deny_and_not_ask()
    {
        // Tier 2 is the one that ASKS about shells, and is where the rule appeared to work: the
        // handler was consulted and refused. That made the hole at tier 3 invisible.
        var policy = RemotePolicy.ForRemoteRun(Tier(2));

        Assert.NotEqual(PermissionDecision.Ask, Decide("run_powershell", policy));
        Assert.Equal(PermissionDecision.Deny, Decide("run_powershell", policy));
    }

    /// <summary>
    /// Everything else is left exactly as the workspace's owner set it. A rule that quietly
    /// tightened the file tools as well would be a different feature, and one nobody asked for.
    /// </summary>
    [Fact]
    public void Nothing_but_the_shells_is_touched()
    {
        var original = Tier(3);
        var remote = RemotePolicy.ForRemoteRun(original);

        Assert.Equal(original.Level, remote.Level);
        Assert.Equal(original.Allow, remote.Allow);
        Assert.Equal(original.AskBefore, remote.AskBefore);
        Assert.Equal(Decide("write_file", original), Decide("write_file", remote));
    }

    /// <summary>
    /// A workspace that already denies something keeps denying it. Replacing the deny list rather
    /// than adding to it would be a rule that loosened one as it tightened another.
    /// </summary>
    [Fact]
    public void An_existing_denial_survives()
    {
        var strict = Tier(3) with { Deny = ["docker"] };
        var remote = RemotePolicy.ForRemoteRun(strict);

        Assert.Equal(PermissionDecision.Deny, Decide("docker", remote));
        Assert.Equal(PermissionDecision.Deny, Decide("run_powershell", remote));
    }

    /// <summary>Applied twice - by a caller that did it and a caller that was not sure - is once.</summary>
    [Fact]
    public void Applying_it_twice_changes_nothing()
    {
        var once = RemotePolicy.ForRemoteRun(Tier(3));
        var twice = RemotePolicy.ForRemoteRun(once);

        Assert.Equal(once.Deny, twice.Deny);
    }
}
