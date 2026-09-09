namespace Enactive.Remote.Host;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// What a run started from the web is allowed to do, whatever its workspace says.
///
/// <para><b>This is where the shell rule is actually enforced.</b>
/// <see cref="RemoteDecisionHandler"/> refuses a shell too, and for five weeks that looked like
/// enough - but a decision handler only ever sees what the policy decided to ASK about. At the
/// Autonomous tier the policy asks about nothing, so the handler was never consulted, and a task
/// started from a phone ran <c>run_powershell</c> in a workspace saved at that tier. The rule that
/// <c>REMOTE_DESIGN.md</c> §5.4 calls the single most important line in the document, and that the
/// panel states as fact to the owner, held only for the tiers that happened to ask.</para>
///
/// <para>Denying is not asking, and that distinction is the whole fix. A denied tool is refused by
/// the engine before anything is invoked; an asked tool is refused by whoever answers. Only the
/// first is a rule.</para>
///
/// <para>The handler's refusal stays, and is now the second of three: this policy stops the call,
/// the handler refuses any request that still reaches it, and the gateway refuses a remote ANSWER
/// to a shell approval. Each covers a different way the others could be wrong - a policy assembled
/// somewhere new, a handler installed without this, a panel that offers a button it should not.
/// </para>
/// </summary>
public static class RemotePolicy
{
    /// <summary>The tools this refuses. The same list <see cref="ShellTools.IsShell"/> answers for.</summary>
    public static readonly IReadOnlyList<string> Shells = ["run_command", "run_powershell"];

    /// <summary>
    /// The same policy with every shell denied outright.
    ///
    /// <para>Applied to the policy a remote run is composed with, so it holds at every tier and for
    /// every workspace - including one whose owner deliberately set Autonomous, which is a decision
    /// about work they start at the machine and was never a decision about the network.</para>
    /// </summary>
    public static PermissionPolicy ForRemoteRun(PermissionPolicy policy)
        => policy with
        {
            Deny = policy.Deny
                .Concat(Shells)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
}
