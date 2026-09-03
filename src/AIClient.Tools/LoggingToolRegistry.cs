namespace AIClient.Tools;

using System.Diagnostics;
using System.Text;
using AIClient.Core.Diagnostics;
using AIClient.Core.Permissions;
using AIClient.Core.Tools;

/// <summary>
/// Decorates any <see cref="IToolRegistry"/> to log the full tool traffic: the complete arguments a
/// tool is invoked with, and the complete <see cref="ToolResult"/> (output/error/artifacts/metadata)
/// with timing. The orchestrator's ToolInvoked/ToolResult events carry only compacted summaries — this
/// is the un-truncated view. No individual tool is touched; the registry is the single choke point.
/// </summary>
public sealed class LoggingToolRegistry : IToolRegistry
{
    private readonly IToolRegistry _inner;
    private readonly ILogSink _log;

    public LoggingToolRegistry(IToolRegistry inner, ILogSink log)
    {
        _inner = inner;
        _log = log;
    }

    public IReadOnlyList<ToolDefinition> Definitions => _inner.Definitions;

    public PermissionLevel RequiredLevelOf(string toolName) => _inner.RequiredLevelOf(toolName);

    public async Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        _log.Info(LogSource.Tool, $"tool → {call.Name}", call.ArgumentsJson, call.Name);

        var sw = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await _inner.InvokeAsync(call, ctx, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error(LogSource.Tool,
                $"tool ✗ {call.Name} threw after {sw.ElapsedMilliseconds} ms: {ex.Message}",
                ex.ToString(), call.Name);
            throw;
        }
        sw.Stop();

        _log.Write(result.Success ? LogLevel.Info : LogLevel.Warn, LogSource.Tool,
            $"tool {(result.Success ? "✓" : "✗")} {call.Name} ({sw.ElapsedMilliseconds} ms)",
            RenderResult(result), call.Name);
        return result;
    }

    private static string RenderResult(ToolResult r)
    {
        var sb = new StringBuilder();
        sb.Append("success=").Append(r.Success).AppendLine();
        if (!string.IsNullOrEmpty(r.Output)) sb.Append("output: ").AppendLine(r.Output);
        if (!string.IsNullOrEmpty(r.Error)) sb.Append("error: ").AppendLine(r.Error);
        if (r.Artifacts.Count > 0)
            sb.Append("artifacts: ").AppendLine(string.Join(", ", r.Artifacts.Select(a => $"{a.Kind}:{a.RelativePath}")));
        if (r.Metadata.Count > 0)
            foreach (var kv in r.Metadata)
                sb.Append("  ").Append(kv.Key).Append(" = ").AppendLine(kv.Value?.ToString() ?? "null");
        return sb.ToString().TrimEnd();
    }
}
