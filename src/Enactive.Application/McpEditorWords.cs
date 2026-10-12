namespace Enactive.Application;

/// <summary>
/// What an MCP server's editor says, in the window's editor (McpEditViewModel) and the service's (McpEditor.razor)
/// alike. The two editors differ in how they take credentials - the window's shows them, the service's never - and say
/// the same things of a server: written twice, the words drifted.
/// </summary>
internal static class McpEditorWords
{
    /// <summary>On opening a server whose stored credentials cannot be decrypted, which is off until they are re-entered.</summary>
    public const string CredentialsUnavailable =
        "Stored credentials could not be decrypted. Re-enter environment variables and headers before using these settings.";

    /// <summary>While Test connection runs.</summary>
    public const string Connecting = "Connecting and listing tools…";
}
