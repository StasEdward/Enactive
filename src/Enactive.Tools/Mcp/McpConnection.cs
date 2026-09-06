namespace Enactive.Tools.Mcp;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enactive.Core.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>A run owns its connections; changing settings cannot replace clients in flight.</summary>
public sealed class McpConnection : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly McpServerConfig _config;
    public IReadOnlyList<ITool> Tools { get; }

    private McpConnection(McpClient client, McpServerConfig config, IEnumerable<McpClientTool> tools)
    {
        _client = client;
        _config = config;
        Tools = tools.Select(t => (ITool)new McpTool(this, t)).ToArray();
    }

    public static async Task<McpConnection> ConnectAsync(McpServerConfig config, string workspaceRoot, CancellationToken ct,
        HttpClient? httpClient = null)
    {
        config = config.Clone();
        if (config.Validate() is { } error) throw new ArgumentException(error);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        IClientTransport transport;
        if (config.Transport == McpTransportKind.Stdio)
        {
            var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var pair in config.Environment) env[pair.Key] = pair.Value;
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Id, Command = config.Command, Arguments = config.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? workspaceRoot : config.WorkingDirectory,
                InheritEnvironmentVariables = false, EnvironmentVariables = env,
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            });
        }
        else
        {
            var options = new HttpClientTransportOptions
            {
                Name = config.Id, Endpoint = new Uri(config.Url), AdditionalHeaders = config.Headers,
                ConnectionTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            transport = httpClient is null ? new HttpClientTransport(options) : new HttpClientTransport(options, httpClient);
        }

        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "Enactive", Version = "0.1.0" },
                InitializationTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            }, cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            return new McpConnection(client, config, tools);
        }
        catch
        {
            if (client is not null) await client.DisposeAsync();
            else if (transport is IAsyncDisposable disposable) await disposable.DisposeAsync();
            throw;
        }
    }

    // Provider-safe, deterministic, <=64 chars; the hash distinguishes sanitized/truncated names.
    public static string ToolName(string serverId, string remoteName)
    {
        var readable = new string(remoteName.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (readable.Length > 19) readable = readable[..19];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remoteName)))[..12].ToLowerInvariant();
        return $"mcp__{serverId}__{readable}_{hash}";
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private sealed class McpTool : ITool
    {
        private readonly McpConnection _connection;
        private readonly string _remoteName;
        public ToolDefinition Definition { get; }
        public Enactive.Core.Permissions.PermissionLevel RequiredLevel => Enactive.Core.Permissions.PermissionLevel.Execute;
        public bool RequiresApproval => _connection._config.RequireApproval;

        public McpTool(McpConnection connection, McpClientTool tool)
        {
            _connection = connection;
            _remoteName = tool.Name;
            Definition = new(ToolName(connection._config.Id, tool.Name),
                $"MCP server '{connection._config.Id}', tool '{tool.Name}'. {tool.Description}", tool.JsonSchema.GetRawText());
        }

        public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_connection._config.TimeoutSeconds));
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return ToolResults.Fail("MCP arguments must be a JSON object.");
                var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                var result = await _connection._client.CallToolAsync(_remoteName, args, cancellationToken: timeout.Token);
                var output = Render(result);
                return result.IsError == true ? ToolResults.Fail(output, output) : ToolResults.Ok(output);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Never retry: a timed-out call may already have changed remote state.
                return ToolResults.Fail($"MCP '{_connection._config.Id}/{_remoteName}' timed out. Its remote outcome is unknown; inspect before retrying.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Transport exception messages may include URLs or credentials. Keep them out of prompts/logs.
                return ToolResults.Fail($"MCP '{_connection._config.Id}/{_remoteName}' failed ({ex.GetType().Name}). Check the server connection; the call was not retried.");
            }
        }
    }

    public static string Render(CallToolResult result)
    {
        var text = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock t) text.AppendLine(t.Text);
            else text.AppendLine($"[MCP content type '{block.Type}' is not displayed by this text-only client.]");
            if (text.Length > 32000) break;
        }
        if (result.StructuredContent is { } structured) text.AppendLine(structured.GetRawText());
        if (text.Length == 0) text.Append("MCP call completed without text content.");
        return text.Length > 32000 ? text.ToString(0, 32000) + "\n… (truncated)" : text.ToString().TrimEnd();
    }
}
