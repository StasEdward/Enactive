namespace Enactive.Agents;

using Enactive.Core.Tools;

/// <summary>Invokes an already-authorized call and captures its workspace revision interval.</summary>
internal static class ToolInvocation
{
    internal sealed record Result(ToolResult Value, long? Before, long? After);

    public static async Task<Result> ExecuteAsync(ToolCall call, IToolRegistry tools,
        ToolContext context, CancellationToken ct)
    {
        var before = tools.WorkspaceVersion(context.WorkspaceId);
        ToolResult result;
        try { result = await tools.InvokeAsync(call, context, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { result = ToolResults.Fail($"{call.Name} threw: {ex.Message}"); }
        return new(result, before, tools.WorkspaceVersion(context.WorkspaceId));
    }
}
