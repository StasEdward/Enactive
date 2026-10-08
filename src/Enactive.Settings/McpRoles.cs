namespace Enactive.Settings;

using Enactive.Core.Tools;

/// <summary>
/// Which of the saved roles may call a given MCP server's tools — the settings-side half of
/// <see cref="McpReach"/>.
///
/// <para><b>Why the screen needs its own copy of the rule.</b> The engine can only say this once a
/// run has started the servers and found nobody to offer them to. The MCP screen can say it while
/// somebody is standing in front of it, before a single child process has been started — and that
/// is where a person can act on it. The rule itself is the role gate's, kept in one place here and
/// pinned by tests, rather than re-derived by eye in a view model.</para>
///
/// <para>Matching is <see cref="ToolAllowlist.ReachesServer"/>, the same rule the engine's role gate
/// asks: <c>*</c> reaches everything, an <c>mcp__…*</c> pattern reaches a server when the pattern and
/// the server's prefix are prefixes of one another - so <c>mcp__*</c> and <c>mcp__dotnet__*</c> both
/// reach <c>dotnet</c>, and <c>mcp__other__*</c> reaches nothing of it - and one of its tools named
/// exactly reaches it too. It used to be its own copy of the rule, and differed from the gate on case.</para>
/// </summary>
public static class McpRoles
{
    /// <summary>Whether this role could call anything from the named server.</summary>
    public static bool Reaches(WorkerConfig worker, string serverId)
        => ToolAllowlist.ReachesServer(worker.Tools, serverId);

    /// <summary>
    /// The enabled servers no role can call — started by every run, offered to nobody.
    ///
    /// <para>Takes the two fields it needs rather than the config type: the roles live here and the
    /// server config lives in <c>Enactive.Tools</c>, which cannot reference this assembly.</para>
    /// </summary>
    public static IReadOnlyList<string> Unreached(
        IEnumerable<WorkerConfig> workers, IEnumerable<(string Id, bool Enabled)> servers)
    {
        var team = workers.ToArray();

        return servers
            .Where(s => s.Enabled && !team.Any(w => Reaches(w, s.Id)))
            .Select(s => s.Id)
            .ToArray();
    }

    /// <summary>
    /// What the MCP pane says about it, or null when every enabled server is reachable.
    /// </summary>
    public static string? Note(IEnumerable<WorkerConfig> workers, IEnumerable<(string Id, bool Enabled)> servers)
    {
        var unreached = Unreached(workers, servers);
        if (unreached.Count == 0)
            return null;

        var many = unreached.Count > 1;

        return $"{string.Join(", ", unreached)} {(many ? "are" : "is")} enabled, so every run starts "
             + $"{(many ? "them" : "it")} — and no worker role names {(many ? "their" : "its")} tools, "
             + $"so nothing can be called. Add \"{McpReach.Prefix}{unreached[0]}__*\" or "
             + $"\"{McpReach.Prefix}*\" to a role under AI · Team, or turn {(many ? "them" : "it")} off.";
    }

}
