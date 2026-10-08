namespace Enactive.Core.Tools;

/// <summary>
/// MCP servers that are connected and whose tools no role may call.
///
/// <para><b>The silence this exists to break.</b> An MCP server is started for EVERY run — a child
/// process per server, and the run waits for each to initialise — and its tools are then filtered
/// by the worker's role like any others. A role that names no <c>mcp__</c> pattern filters all of
/// them out, and nothing anywhere says so: the tools are not "withheld" (<c>ToolOffers</c> never
/// sees them, because the role gate runs first), they simply are not there. Found 2026-09-22 on a
/// machine with two servers enabled for days: no <c>mcp__</c> tool appears anywhere in the log, and
/// two processes were being started per run for nothing.</para>
///
/// <para>It is the same defect as a tool no role names, seen from further out, and this codebase
/// has now met it five times inside its own registry (<c>edit_file</c>, <c>search_files</c>,
/// <c>create_directory</c>, <c>move_file</c>, <c>copy_file</c>) and twice outside it
/// (<c>send_email</c>, and this). The engine cannot fix the configuration, but it can refuse to be
/// quiet about it.</para>
///
/// <para>Per SERVER rather than in total, because "some MCP tools are unreachable" sends somebody
/// to the wrong screen: a run may hold one server the role names and another it does not, and the
/// answer differs by server.</para>
/// </summary>
public static class McpReach
{
    /// <summary>Every MCP tool name starts with this; see <c>McpConnection.ToolName</c>.</summary>
    public const string Prefix = "mcp__";

    /// <summary>The server a tool came from, or null when the name is not an MCP tool's.</summary>
    public static string? ServerOf(string toolName)
    {
        if (!toolName.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var rest = toolName[Prefix.Length..];
        var end = rest.IndexOf("__", StringComparison.Ordinal);
        return end > 0 ? rest[..end] : null;
    }

    /// <summary>
    /// What to say about the servers this run connected and cannot use, or null when there is
    /// nothing to say — no MCP tools at all, or a role that reaches every server holding some.
    /// </summary>
    /// <param name="registered">Every tool name the run's registry holds, MCP and built-in alike.</param>
    /// <param name="allows">The role gate, as the engine reaches it: does this worker carry this tool.</param>
    /// <param name="role">
    /// The role, named in the sentence: a run says it once for each role that cannot reach a server, and two roles that
    /// cannot were one identical sentence, said once for the first and never for the second.
    /// </param>
    public static string? Unreached(IEnumerable<string> registered, Func<string, bool> allows, string? role = null)
    {
        // Per server: how many tools it contributed, and how many of those the role may call.
        var tally = new Dictionary<string, (int Tools, int Allowed)>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in registered)
        {
            if (ServerOf(name) is not { } server)
                continue;

            tally.TryGetValue(server, out var counts);
            tally[server] = (counts.Tools + 1, counts.Allowed + (allows(name) ? 1 : 0));
        }

        var unreachable = tally.Where(p => p.Value.Allowed == 0)
                               .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                               .ToArray();

        if (unreachable.Length == 0)
            return null;

        var named = string.Join(", ", unreachable.Select(p => $"{p.Key} ({p.Value.Tools} tool(s))"));

        // The fix, spelled exactly, because "configure the role" is the sentence somebody reads
        // twice and acts on never. One server's pattern is offered before the blanket one: a run
        // pays for every tool definition it is offered, in every prompt of every step.
        var one = unreachable[0].Key;

        return $"MCP server(s) started and offered to nobody: {named}. {(role is null ? "This worker's role" : $"The {role} role")} names no "
             + $"tool from them, so the run pays to start them and cannot use them. Add "
             + $"\"{Prefix}{one}__*\" (that server) or \"{Prefix}*\" (all of them) to the role under "
             + "Settings → AI → Team, or turn the server off.";
    }
}
