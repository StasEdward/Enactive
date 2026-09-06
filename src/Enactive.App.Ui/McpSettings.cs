namespace Enactive.App.Ui;

using System.Text.Json;
using Enactive.Tools.Mcp;

internal sealed partial class AppSettings
{
    public List<McpServerConfig> McpServers { get; set; } = new();

    private void LoadMcpSecrets()
    {
        foreach (var server in McpServers)
        {
            if (string.IsNullOrEmpty(server.SecretsProtected)) continue;
            try
            {
                var secrets = JsonSerializer.Deserialize<McpSecrets>(Secret.Unprotect(server.SecretsProtected));
                if (secrets?.Environment is null || secrets.Headers is null) throw new InvalidOperationException();
                server.Environment = secrets.Environment;
                server.Headers = secrets.Headers;
            }
            catch { server.Enabled = false; server.CredentialsUnavailable = true; }
        }
    }

    private void SaveMcpSecrets()
    {
        foreach (var server in McpServers)
        {
            // Preserve the original ciphertext until the user explicitly edits or removes this server.
            if (server.CredentialsUnavailable) continue;
            if (server.Environment.Count == 0 && server.Headers.Count == 0)
            { server.SecretsProtected = ""; continue; }
            var encrypted = Secret.Protect(JsonSerializer.Serialize(new McpSecrets(server.Environment, server.Headers)));
            if (!encrypted.StartsWith("dpapi:", StringComparison.Ordinal))
                throw new InvalidOperationException("MCP credentials could not be encrypted. Settings were not saved.");
            server.SecretsProtected = encrypted;
        }
    }

    private sealed record McpSecrets(Dictionary<string, string> Environment, Dictionary<string, string> Headers);
}
