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

        var revision = _revisions.GetOrAdd(ctx.WorkspaceId, _ => new Revision());
        var pending = tool.Definition.WorkspaceEffect != WorkspaceEffect.None;
        if (pending)
            lock (revision) { revision.Pending++; revision.Version++; }
        try
        {
            var result = await tool.InvokeAsync(call.ArgumentsJson, ctx, ct);
            var effect = result.DidNotRun ? WorkspaceEffect.None
                : result.WorkspaceEffect ?? (result.Success || tool.Definition.WorkspaceEffect == WorkspaceEffect.None
                    ? tool.Definition.WorkspaceEffect : WorkspaceEffect.Unknown);
            if (!pending && effect != WorkspaceEffect.None)
                lock (revision) revision.Version++;
            return result with { WorkspaceEffect = effect };
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
