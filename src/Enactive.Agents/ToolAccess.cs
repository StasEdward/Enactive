namespace Enactive.Agents;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using static ToolCallParsing;

/// <summary>Shared admission rules and approval binding; no invocation, transcript or journal side effects.</summary>
internal sealed class ToolAccess(IToolRegistry tools, IPermissionEngine permissions)
{
    public PermissionDecision Evaluate(PermissionPolicy policy, string tool, ToolOffer offer)
    {
        var gate = permissions.Evaluate(policy, tool, tools.RequiredLevelOf(tool));
        if (gate == PermissionDecision.Allow && tools.RequiresApprovalOf(tool)) gate = PermissionDecision.Ask;
        if (offer.Withholds(tool)) gate = PermissionDecision.Deny;
        return gate;
    }

    public bool CanRunRead(ToolCall call, Worker worker, PermissionPolicy policy, ToolOffer offer)
        => tools.Definitions.Any(d => string.Equals(d.Name, call.Name, StringComparison.OrdinalIgnoreCase)
            && d.ParallelRead && d.WorkspaceEffect == WorkspaceEffect.None)
        && tools.RequiredLevelOf(call.Name) == PermissionLevel.Observe
        && tools.DefinitionOf(call.Name)?.Kind != ToolKind.Command && Allows(worker, call.Name)
        && !offer.Withholds(call.Name) && !tools.RequiresApprovalOf(call.Name)
        && Evaluate(policy, call.Name, offer) == PermissionDecision.Allow;

    public DecisionRequest Approval(ToolCall call, Guid taskId, Guid runId, string root)
        => new(taskId, $"Run tool '{call.Name}'?", $"Arguments: {Compact(call.ArgumentsJson)}",
            [new DecisionOption("allow", "Allow"), new DecisionOption("deny", "Deny")],
            RecommendedOptionId: "allow",
            Subject: tools.RequiresApprovalOf(call.Name) ? null : call.Name,
            FullDetail: DescribeCall(call), SessionOnly: ShellTools.IsShell(call.Name),
            Action: new BoundAction(runId, call.Id, call.Name, call.ArgumentsJson, root));

    // Both ordinary and geography approvals share the host's gate. This helper never owns/disposes it.
    public static async Task<DecisionOutcome> AskAsync(IDecisionHandler handler, SemaphoreSlim gate,
        DecisionRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await handler.RequestAsync(request, ct); }
        finally { gate.Release(); }
    }

    public static PermissionPolicy EffectivePolicy(PermissionPolicy policy, Worker worker)
    {
        var level = (PermissionLevel)Math.Min((int)policy.Level, (int)worker.DefaultLevel);
        return level == policy.Level ? policy : policy with { Level = level };
    }

    internal string? NearestTool(string wrong, Worker worker)
    {
        static string Bare(string name)
            => name.Replace("-", "").Replace("_", "").Replace(".", "");

        var bare = Bare(wrong);

        return tools.Definitions
            .Where(d => Allows(worker, d.Name))
            .Select(d => d.Name)
            .FirstOrDefault(name => string.Equals(Bare(name), bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether this worker's role names the tool - see <see cref="ToolAllowlist"/>, which every reader of a role asks.</summary>
    public static bool Allows(Worker worker, string tool) => ToolAllowlist.Allows(worker.ToolAllowlist, tool);
}
