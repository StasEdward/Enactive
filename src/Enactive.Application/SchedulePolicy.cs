namespace Enactive.Application;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Settings;

/// <summary>
/// The policy a schedule is saved with: the tier picked in the form, with the shell rule from Settings on top.
///
/// <para><b>From the settings as they are when it is asked, not as they were when the window opened.</b>
/// Saving in Settings puts a NEW settings object in place of the live one. The Schedules window was handed the
/// object and kept it, so a schedule saved after "never run commands" had been chosen was still given the rule
/// from before - and a scheduled run then ran commands on a machine whose settings say never to, with nobody
/// there to see it. So this holds the way to ask, and asks each time.</para>
/// </summary>
internal sealed class SchedulePolicy(Func<AppSettings> current)
{
    public PermissionPolicy For(string? tier)
        => EngineComposition.PolicyFor(current(), AutonomyTiers.Parse(tier) ?? 2);

    /// <summary>
    /// The policy for the tier a policy says it is at, made again from the settings now: whatever else that policy
    /// allowed is not kept. For a service handed a schedule by a page - taken as given, a looser policy than its settings
    /// allow (no shell rule on it) would be saved and run unattended.
    /// </summary>
    public PermissionPolicy For(PermissionLevel level)
        => EngineComposition.PolicyFor(current(), AutonomyTiers.Clamp((int)level));
}
