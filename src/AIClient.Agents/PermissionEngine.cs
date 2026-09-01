namespace AIClient.Agents;

using AIClient.Core.Permissions;

/// <summary>
/// Evaluates a tool call against a workspace policy: an explicit AskBefore entry always asks; a tool
/// within the policy's autonomy level and allow-list runs automatically; anything above the granted
/// level asks for a one-off approval.
/// </summary>
public sealed class PermissionEngine : IPermissionEngine
{
    public PermissionDecision Evaluate(PermissionPolicy policy, string toolName, PermissionLevel requiredLevel)
    {
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
