namespace Enactive.Tools.Mcp;

using Enactive.Core.Tools;
using Enactive.Core.Permissions;

/// <summary>Composite registry with run-scoped MCP lifetime. Connection errors fail the run explicitly.</summary>
public sealed class McpRunTools : IToolRegistry, IAsyncDisposable
{
    private readonly IToolRegistry _builtIn;
    private readonly List<McpConnection> _connections = new();
    private ToolRegistry _remote = new(Array.Empty<ITool>());
    private McpRunTools(IToolRegistry builtIn) => _builtIn = builtIn;
    public IReadOnlyList<ToolDefinition> Definitions => _builtIn.Definitions.Concat(_remote.Definitions).ToArray();
    // Both counters are monotonic; either pending/unknown component makes the combined view unknown.
    public long? WorkspaceVersion(Guid workspaceId)
        => _builtIn.WorkspaceVersion(workspaceId) + _remote.WorkspaceVersion(workspaceId);
    private bool IsRemote(string name) => name.StartsWith("mcp__", StringComparison.Ordinal);
    public PermissionLevel RequiredLevelOf(string name) => (IsRemote(name) ? _remote : _builtIn).RequiredLevelOf(name);
    public bool RequiresApprovalOf(string name) => (IsRemote(name) ? _remote : _builtIn).RequiresApprovalOf(name);
    public Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
        => (IsRemote(call.Name) ? _remote : _builtIn).InvokeAsync(call, ctx, ct);

    public static async Task<McpRunTools> ConnectAsync(IToolRegistry builtIn, IEnumerable<McpServerConfig> configs,
        string root, CancellationToken ct)
    {
        var result = new McpRunTools(builtIn);
        try
        {
            var enabled = configs.Where(c => c.Enabled).Select(c => c.Clone()).ToArray();
            if (enabled.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InvalidOperationException("MCP server IDs must be unique.");
            foreach (var config in enabled)
            {
                try { result._connections.Add(await McpConnection.ConnectAsync(config, root, ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { throw new InvalidOperationException($"MCP server '{config.Id}' could not connect ({ex.GetType().Name}). Check its settings or disable it."); }
            }
            result._remote = new ToolRegistry(result._connections.SelectMany(c => c.Tools));
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    /// <summary>
    /// What connected, and how much it brought — for the log line at the call site.
    ///
    /// <para>There was none. A server that started fine and a server that was never configured
    /// looked identical from the log, and the only evidence a run had MCP at all was a tool call
    /// that came from one. That is the wrong way round: the connection is a fact about the run, and
    /// the call is a choice the model may never make.</para>
    /// </summary>
    public string Summary()
        => _connections.Count == 0
            ? "no MCP servers are enabled"
            : "MCP connected: " + string.Join(", ",
                _connections.Select(c => $"{c.Id} ({c.Tools.Count} tool(s))"));

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
            try { await connection.DisposeAsync(); } catch { /* dispose every server even if one failed */ }
        _connections.Clear();
    }
}
