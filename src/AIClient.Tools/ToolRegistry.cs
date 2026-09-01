namespace AIClient.Tools;

using AIClient.Core.Permissions;
using AIClient.Core.Tools;

/// <summary>Holds the available tools and dispatches calls by name.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyDictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
        => _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToArray();

    public PermissionLevel RequiredLevelOf(string toolName)
        => _tools.TryGetValue(toolName, out var tool) ? tool.RequiredLevel : PermissionLevel.Execute;

    public Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return Task.FromResult(ToolResults.Fail($"Unknown tool '{call.Name}'."));

        return tool.InvokeAsync(call.ArgumentsJson, ctx, ct);
    }
}
