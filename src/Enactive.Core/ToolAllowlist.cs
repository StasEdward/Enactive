namespace Enactive.Core.Tools;

/// <summary>
/// What a role's list of tools reaches - the one answer the engine's role gate, the settings screens and
/// the advice a worker is given all ask for.
///
/// <para><b>Why one.</b> The question was answered in six places, and they disagreed where nobody looked.
/// A role naming <c>MCP__dotnet__build</c> was let through by the engine (exact names ignored case) while
/// the MCP screen said no role reached the server (its patterns did not); and the worker was advised to
/// use tools the gate refused, because the advice read the list with the implied tools added and the
/// gate read it as saved. A role means one thing, or the screens and the advice are about some other role.</para>
///
/// <para><b>The rules.</b> Case is ignored everywhere - for names and for patterns alike, as MCP server
/// ids are already unique ignoring case. <c>*</c> on its own is every tool. A trailing <c>*</c> is a
/// pattern only after <c>mcp__</c>: an MCP server's tools are not known until it starts, so a role names
/// them by prefix. Built-in tools are granted by name, on purpose - <c>send_email</c> and
/// <c>delete_file</c> are each given by a person deciding to - and <c>write_*</c> would quietly hand a
/// role every tool of that name added later, so anything else ending in <c>*</c> is just a name.</para>
///
/// <para><b>What it is not for.</b> Lists that are not a role's, read by rules of their own on purpose: a
/// permission policy (<c>PermissionPolicy</c> - the autonomy tier's and a template's, matched by the permission
/// engine), and the commands agreed for one task (<c>TaskActionPolicy</c>), which a model writes and which match
/// a tool's name exactly - there a <c>*</c> must not come to mean every tool. The settings' old migrations read
/// role lists in their own way too, and stay as they ran.</para>
/// </summary>
public static class ToolAllowlist
{
    /// <summary>The entry that grants every tool there is.</summary>
    public const string Everything = "*";

    /// <summary>Whether the list grants every tool.</summary>
    public static bool GrantsEverything(IEnumerable<string> list) => list.Any(entry => entry == Everything);

    /// <summary>
    /// Whether the list carries this entry - by the same case rule, but as an entry and not for what it reaches:
    /// <c>mcp__*</c> does not hold <c>mcp__dotnet__build</c>. What the role editor ticks. It read the list with
    /// case, while the tools it offers are told apart without it, so a role saved with <c>Write_File</c> showed an
    /// unticked <c>write_file</c>, and Save took the tool away.
    /// </summary>
    public static bool Holds(IEnumerable<string> list, string entry)
        => list.Any(held => string.Equals(held, entry, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the list lets a role call this tool.</summary>
    public static bool Allows(IEnumerable<string> list, string tool) => list.Any(entry => Matches(entry, tool));

    /// <summary>
    /// Whether the list lets a role call anything the named MCP server could offer: every tool, a pattern
    /// that covers the server's prefix or a part of it, or one of the server's tools by name. Asked before
    /// the server has started, so it is the same as <see cref="Allows"/> for some tool of that server.
    /// </summary>
    public static bool ReachesServer(IEnumerable<string> list, string serverId)
    {
        var prefix = $"{McpReach.Prefix}{serverId}__";

        return list.Any(entry =>
            entry == Everything
            || (IsMcpPattern(entry) is { } head
                && (prefix.StartsWith(head, StringComparison.OrdinalIgnoreCase)
                    || head.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            || (!entry.EndsWith('*') && entry.Length > prefix.Length
                && entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool Matches(string entry, string tool)
        => entry == Everything
           || (IsMcpPattern(entry) is { } head
               ? tool.StartsWith(head, StringComparison.OrdinalIgnoreCase)
               : string.Equals(entry, tool, StringComparison.OrdinalIgnoreCase));

    /// <summary>The prefix an <c>mcp__…*</c> pattern stands for, or null when the entry is a name.</summary>
    private static string? IsMcpPattern(string entry)
        => entry.EndsWith('*') && entry.StartsWith(McpReach.Prefix, StringComparison.OrdinalIgnoreCase)
            ? entry[..^1]
            : null;
}
