namespace Enactive.Server.Runs;

using Enactive.Tools.Mcp;

/// <summary>
/// An MCP server as the settings page edits it: lists as lines, and its credentials - the environment and the headers,
/// which hold tokens - never put on the page. The page shows which names are set and takes new ones; nothing typed
/// leaves them as they are, so opening and saving the settings does not wipe a token nobody could see to type back.
/// </summary>
internal sealed class McpServerForm
{
    public string Arguments { get; set; } = "";
    /// <summary>NAME=value, one per line: replaces the environment the server has, when anything is typed.</summary>
    public string NewEnvironment { get; set; } = "";
    /// <summary>Name: value, one per line: replaces the headers the server has, when anything is typed.</summary>
    public string NewHeaders { get; set; } = "";
    /// <summary>Takes the environment and headers away, for a server that no longer needs them.</summary>
    public bool ClearCredentials { get; set; }
    public string Timeout { get; set; } = "";

    /// <summary>The names the server's credentials carry - what the page may say about them.</summary>
    public static string NamesSet(McpServerConfig config)
    {
        if (config.CredentialsUnavailable)
            return "cannot be decrypted - enter them again";
        var names = config.Environment.Keys.Select(k => k + "=…").Concat(config.Headers.Keys.Select(k => k + ": …")).ToList();
        return names.Count == 0 ? "none set" : string.Join(", ", names);
    }

    public static McpServerForm Of(McpServerConfig config) => new()
    {
        Arguments = string.Join("\n", config.Arguments),
        Timeout = config.TimeoutSeconds.ToString()
    };

    /// <summary>Writes what was typed into <paramref name="config"/>; what is wrong with it, or null.</summary>
    public string? ApplyTo(McpServerConfig config)
    {
        config.Id = config.Id.Trim();
        config.Command = config.Command.Trim();
        config.WorkingDirectory = config.WorkingDirectory.Trim();
        config.Url = config.Url.Trim();
        // One per line, not split on spaces: an argument may hold one ("C:\Program Files\...").
        config.Arguments = Lines(Arguments).ToList();
        config.TimeoutSeconds = int.TryParse(Timeout.Trim(), out var seconds) ? seconds : 0;

        if (ClearCredentials)
        {
            config.Environment = new();
            config.Headers = new();
            config.CredentialsUnavailable = false;
        }
        if (NewEnvironment.Trim().Length > 0)
        {
            if (Pairs(NewEnvironment, '=') is not { } environment)
                return $"MCP '{config.Id}': the environment is NAME=value, one per line.";
            config.Environment = environment;
            config.CredentialsUnavailable = false;
        }
        if (NewHeaders.Trim().Length > 0)
        {
            if (Pairs(NewHeaders, ':') is not { } headers)
                return $"MCP '{config.Id}': the headers are Name: value, one per line.";
            config.Headers = headers;
            config.CredentialsUnavailable = false;
        }
        return config.Validate() is { } problem ? $"MCP '{config.Id}': {problem}" : null;
    }

    private static IEnumerable<string> Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, string>? Pairs(string text, char separator)
    {
        var pairs = new Dictionary<string, string>();
        foreach (var line in Lines(text))
        {
            var at = line.IndexOf(separator);
            if (at <= 0)
                return null;
            pairs[line[..at].Trim()] = line[(at + 1)..].Trim();
        }
        return pairs;
    }
}
