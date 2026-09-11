namespace Enactive.Core.Permissions;

/// <summary>
/// Evaluates a tool call against a workspace policy: an explicit AskBefore entry always asks; a tool
/// within the policy's autonomy level and allow-list runs automatically; anything above the granted
/// level asks for a one-off approval.
///
/// <para><b>In Core, and that is the point.</b> It lived in <c>Enactive.Agents</c>, which meant the
/// only thing that could ask "what would happen if this run called that tool" was the run itself -
/// at the moment of the call, with nobody left to tell. The schedules window needs the same answer
/// BEFORE anything is saved, and a second copy of the rule written there would be a rule enforced
/// in two places and tested in one, which after the next refactor is a rule enforced in one.</para>
/// </summary>
public sealed class PermissionEngine : IPermissionEngine
{
    public PermissionDecision Evaluate(PermissionPolicy policy, string toolName, PermissionLevel requiredLevel)
    {
        // Deny first, and it is not overridable. A tool that is both denied and listed as
        // ask-before is denied: the narrower of two answers is the one that was meant, and asking
        // about something that is forbidden invites an approval that cannot be honoured.
        if (Contains(policy.Deny, toolName))
            return PermissionDecision.Deny;

        if (Contains(policy.AskBefore, toolName))
            return PermissionDecision.Ask;

        var allowed = Contains(policy.Allow, "*") || Contains(policy.Allow, toolName);
        if (allowed && (int)requiredLevel <= (int)policy.Level)
            return PermissionDecision.Allow;

        // The tool needs more autonomy than the policy grants — let the user approve it once.
        return PermissionDecision.Ask;
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        foreach (var item in list)
        {
            if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
