namespace Enactive.Tools;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using System.Collections.Concurrent;

/// <summary>Holds the available tools and dispatches calls by name.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly ConcurrentDictionary<Guid, Revision> _revisions = new();

    private sealed class Revision
    {
        public long Version;
        public int Pending;
    }

    public long? WorkspaceVersion(Guid workspaceId)
    {
        var revision = _revisions.GetOrAdd(workspaceId, _ => new Revision());
        lock (revision) return revision.Pending == 0 ? revision.Version : null;
    }

    public ToolRegistry(IEnumerable<ITool> tools)
        => _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToArray();
    public ToolDefinition? DefinitionOf(string name) => _tools.TryGetValue(name, out var tool) ? tool.Definition : null;
    public bool RequiresApprovalOf(string toolName) => _tools.TryGetValue(toolName, out var tool) && tool.RequiresApproval;

    public PermissionLevel RequiredLevelOf(string toolName)
        => _tools.TryGetValue(toolName, out var tool) ? tool.RequiredLevel : PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        // A name with no tool behind it: nothing was attempted, so Unreadable rather than Fail.
        // The orchestrator answers this before dispatch, with the nearest real spelling (9bk); this
        // is the same sentence for every caller that does not come through that gate - the success
        // criteria, a review's own checks, a remote host.
        if (!_tools.TryGetValue(call.Name, out var tool))
            return ToolResults.Unreadable($"There is no tool called '{call.Name}'. Nothing ran.");

        if (ctx.Context?.Restrictions.Any(r => r.Effect == ForbiddenTaskEffect.FileDeletion) == true
            && RecordedOperations.DeletesFiles(call, tool.Definition))
            return ToolResults.Unreadable("Refused by the original task's no-deletion restriction. Nothing ran. "
                + "Approval does not override this restriction. Restore contents using an allowed write/edit operation; do not delete the file.")
                with { WorkspaceEffect = WorkspaceEffect.None, Metadata = new Dictionary<string, object?> { ["taskConstraintRefusal"] = true } };

        string? policyError = null;
        if (ctx.Context?.ActionPolicy is { } policy)
        {
            if (!policy.AllowedTools.Contains(tool.Definition.Name, StringComparer.Ordinal))
                policyError = "Tool is not in allowed_tools.";
            else if (tool.Definition.Kind == ToolKind.Command
                && tool.Definition.CommandPolicy == CommandPolicySyntax.PowerShell
                && tool is ICommandPolicyValidator validator)
                policyError = await validator.ValidatePolicyAsync(call.ArgumentsJson, policy, ctx.Context.Restrictions, ct);
            else if (!policy.Allows(call, tool.Definition))
                policyError = "Command is outside the allowed families or contains unsupported shell syntax.";
        }
        if (policyError is not null)
            return ToolResults.Unreadable("Refused by the task action policy. Nothing ran. " + policyError
                + " Choose an allowed tool/command; approval cannot override the task contract.",
                metadata: new Dictionary<string, object?> { ["taskConstraintRefusal"] = true })
                with { WorkspaceEffect = WorkspaceEffect.None };

        var revision = _revisions.GetOrAdd(ctx.WorkspaceId, _ => new Revision());
        var pending = tool.Definition.WorkspaceEffect != WorkspaceEffect.None;
        if (pending)
            lock (revision) { revision.Pending++; revision.Version++; }
        try
        {
            // Declared types, not strings (ArgumentTypes); the call is recorded as the model sent it.
            var result = await tool.InvokeAsync(ArgumentTypes.Coerce(tool.Definition.JsonSchema, call.ArgumentsJson), ctx, ct);
            var effect = result.DidNotRun ? WorkspaceEffect.None
                : result.WorkspaceEffect ?? (result.Success || tool.Definition.WorkspaceEffect == WorkspaceEffect.None
                    ? tool.Definition.WorkspaceEffect : WorkspaceEffect.Unknown);
            if (!pending && effect != WorkspaceEffect.None)
                lock (revision) revision.Version++;
            return result with
            {
                WorkspaceEffect = effect,
                ChangedPaths = effect == WorkspaceEffect.Changed
                    ? (result.ChangedPaths ?? ToolEffects.Paths(tool.Definition, call.ArgumentsJson))?.ToArray()
                    : null
            };
        }
        catch
        {
            // Even a failing call can have changed files before throwing.
            if (!pending) lock (revision) revision.Version++;
            throw;
        }
        finally
        {
            if (pending)
                lock (revision) { revision.Pending--; revision.Version++; }
        }
    }
}
