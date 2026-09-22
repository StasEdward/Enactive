namespace Enactive.Tools;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>Holds the available tools and dispatches calls by name.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyDictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
        => _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToArray();
    public bool RequiresApprovalOf(string toolName) => _tools.TryGetValue(toolName, out var tool) && tool.RequiresApproval;

    public PermissionLevel RequiredLevelOf(string toolName)
        => _tools.TryGetValue(toolName, out var tool) ? tool.RequiredLevel : PermissionLevel.Execute;

    public Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        // A name with no tool behind it: nothing was attempted, so Unreadable rather than Fail.
        // The orchestrator answers this before dispatch, with the nearest real spelling (9bk); this
        // is the same sentence for every caller that does not come through that gate - the success
        // criteria, a review's own checks, a remote host.
        if (!_tools.TryGetValue(call.Name, out var tool))
            return Task.FromResult(ToolResults.Unreadable(
                $"There is no tool called '{call.Name}'. Nothing ran."));

        return tool.InvokeAsync(call.ArgumentsJson, ctx, ct);
    }
}
