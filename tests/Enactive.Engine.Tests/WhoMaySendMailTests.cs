namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Settings;
using Xunit;

/// <summary>
/// Who may call <c>send_email</c>, and how a person changes that.
///
/// <para><b>The defect these were written for.</b> An SMTP account was filled in, correctly, and
/// no run was ever offered the tool. A role's tool list is SAVED, so adding <c>send_email</c> to
/// the built-in roles reached new installations and nobody else — the fifth time this codebase has
/// shipped a tool that no existing role named (see <c>WorkerTools</c> for the other four). Mail
/// cannot be handed out by the migration that fixed those: every entry there is a capability the
/// worker already holds under another name, and nothing a worker holds implies sending a message
/// that cannot be recalled. So it is granted by a person, and <see cref="MailRoles"/> is the rule
/// they are granted it BY.</para>
/// </summary>
public sealed class WhoMaySendMailTests
{
    private static WorkerConfig Role(string id, PermissionLevel level, params string[] tools)
        => new() { Id = id, Role = id, Level = level, Tools = tools.ToList() };

    private static List<WorkerConfig> Team() =>
    [
        Role("developer", PermissionLevel.Execute, "write_file", "run_command"),
        Role("reviewer", PermissionLevel.Observe, "read_file"),
        Role("ops", PermissionLevel.Execute, "run_command")
    ];

    /// <summary>A role that may run things and is given the tool, under the role, can send.</summary>
    [Fact]
    public void A_role_given_the_tool_can_send()
    {
        var team = Team();
        team[0].Tools.Add(MailRoles.Tool);

        Assert.True(MailRoles.Carries(team[0]));
        Assert.True(MailRoles.AnyoneCanSend(team));
    }

    /// <summary>
    /// A read-only role cannot send even when its list names the tool. The tool requires Execute at
    /// call time, so the name would be a promise that can only come out no - and the pane must not
    /// count it as somebody who can send.
    /// </summary>
    [Fact]
    public void A_role_that_may_not_run_anything_cannot_send()
    {
        var team = Team();
        team[1].Tools.Add(MailRoles.Tool);

        Assert.False(MailRoles.CanCarry(team[1]));
        Assert.False(MailRoles.AnyoneCanSend(team));
    }

    /// <summary>A role holding <c>*</c> already has the tool, and is said to by its wildcard.</summary>
    [Fact]
    public void A_role_granted_everything_can_send()
    {
        var team = new List<WorkerConfig> { Role("everything", PermissionLevel.Autonomous, "*") };

        Assert.True(MailRoles.Carries(team[0]));
        Assert.True(MailRoles.Wildcarded(team[0]));
        Assert.True(MailRoles.AnyoneCanSend(team));
    }

    /// <summary>
    /// The state the bug was reported in: a team saved before the tool existed. Nobody can send,
    /// and that is what the pane has to say rather than "send_email may write to …".
    /// </summary>
    [Fact]
    public void A_team_saved_before_the_tool_existed_cannot_send()
        => Assert.False(MailRoles.AnyoneCanSend(Team()));

    /// <summary>
    /// And a NEW installation can, without anybody ticking anything: the built-in roles name it,
    /// so the two halves of this - the defaults and the grant - cannot drift apart unnoticed.
    /// </summary>
    [Fact]
    public void A_fresh_installation_already_has_somebody_who_can_send()
    {
        var seeded = DefaultWorkers.Seed(new ModelRef("ollama", "any"))
            .Select(w => new WorkerConfig
            {
                Id = w.Id, Role = w.Role, Level = w.DefaultLevel, Tools = w.ToolAllowlist.ToList()
            })
            .ToList();

        Assert.True(MailRoles.AnyoneCanSend(seeded));
    }
}
