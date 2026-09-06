namespace Enactive.Tools.Mcp;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public enum McpTransportKind { Stdio, Http }

/// <summary>User-owned connection settings. Runtime credentials are never serialized.</summary>
public sealed class McpServerConfig
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public McpTransportKind Transport { get; set; }
    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
    public string WorkingDirectory { get; set; } = "";
    public string Url { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public bool RequireApproval { get; set; } = true;
    [JsonIgnore] public Dictionary<string, string> Environment { get; set; } = new();
    [JsonIgnore] public Dictionary<string, string> Headers { get; set; } = new();
    public string SecretsProtected { get; set; } = "";
    [JsonIgnore] public bool CredentialsUnavailable { get; set; }

    public McpServerConfig Clone() => new()
    {
        Id = Id, Enabled = Enabled, Transport = Transport, Command = Command,
        Arguments = new(Arguments), WorkingDirectory = WorkingDirectory, Url = Url,
        TimeoutSeconds = TimeoutSeconds, RequireApproval = RequireApproval,
        Environment = new(Environment), Headers = new(Headers), SecretsProtected = SecretsProtected,
        CredentialsUnavailable = CredentialsUnavailable
    };

    public string? Validate()
    {
        if (CredentialsUnavailable) return "Stored MCP credentials cannot be decrypted. Edit the server and re-enter its environment and headers, or remove it.";
        if (Id is null || !Regex.IsMatch(Id, "^[a-z][a-z0-9-]{0,23}$"))
            return "Server ID must be 1–24 lowercase letters, digits or hyphens, starting with a letter.";
        if (!Enum.IsDefined(Transport)) return "Unknown MCP transport.";
        if (TimeoutSeconds is < 1 or > 600) return "Timeout must be between 1 and 600 seconds.";
        if (Transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(Command))
            return "Enter the executable to start the server.";
        if (Transport == McpTransportKind.Http &&
            (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            return "Enter an HTTP(S) endpoint without credentials in the URL.";
        if (Arguments is null || Arguments.Any(a => a is null)) return "Arguments must contain strings.";
        if (Environment is null || Environment.Any(p => p.Value is null || string.IsNullOrWhiteSpace(p.Key) || p.Key.Contains('=') || p.Key.Contains('\0')))
            return "Invalid environment variable name.";
        if (Headers is null || Headers.Any(p => p.Value is null || string.IsNullOrWhiteSpace(p.Key) || p.Key.Any(char.IsWhiteSpace) || p.Value.Contains('\r') || p.Value.Contains('\n')))
            return "Invalid HTTP header.";
        return null;
    }
}
