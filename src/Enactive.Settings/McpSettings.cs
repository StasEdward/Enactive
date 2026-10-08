namespace Enactive.Settings;

using System.Text.Json;
using Enactive.Tools.Mcp;
using Enactive.Secrets;

public sealed partial class AppSettings
{
    public List<McpServerConfig> McpServers { get; set; } = new();

    /// <summary>An MCP server's credentials as one encrypted value - see AppSettings.Secrets().</summary>
    private sealed record McpSecrets(Dictionary<string, string> Environment, Dictionary<string, string> Headers);
}
