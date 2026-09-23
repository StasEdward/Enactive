namespace Enactive.Core.Tools;

/// <summary>
/// Tools the host registered that no role in the team names.
///
/// <para><b>The mirror of <see cref="McpReach"/>, and the side that has cost more.</b> A tool has
/// two halves that must agree: the host puts it in the registry, and a role names it in its
/// allowlist. If they disagree, the tool does not exist as far as any model is concerned — written,
/// tested, documented, and dead. <c>Program.cs</c> has said so in a comment for months without
/// acting on it: <i>"A tool a role names but the host does not register is the same defect as a
/// tool the host registers and no role names, seen from the other side."</i></para>
///
/// <para><b>Five times inside this registry, and every one found by symptom rather than by
/// message:</b></para>
/// <list type="bullet">
/// <item><c>edit_file</c> — registered, named by nobody, so the only way to change a file stayed
/// <c>write_file</c>. A 12B model asked to add one menu entry to a 414-line page returned 168 lines
/// of it; nothing about that was the model misbehaving.</item>
/// <item><c>search_files</c>, <c>create_directory</c>, <c>move_file</c> — added to the default roles
/// and reaching nobody who already had a settings.json, which is everybody. Found when a task
/// started from a phone could not rename a file and correctly gave up. The tool it wanted
/// existed.</item>
/// <item><c>copy_file</c> — the same again, a fifth time.</item>
/// <item><c>send_email</c>, 2026-09-22/23 — three nights in which an agent read this project's own
/// <c>SendEmailTool.cs</c>, decrypted the SMTP password with the DPAPI entropy it found there, and
/// wrote its own mailer, because the safe route was not offered and the unsafe one was.</item>
/// </list>
///
/// <para><b>Why the whole TEAM and not the running role.</b> A role lacking a tool is normal and
/// usually deliberate: the writer may not run shells, the reviewer may not write. That is a
/// decision, not a defect, and warning about it every run would be noise nobody reads. A tool NO
/// role names is different in kind — there is no configuration in which it can ever be used, so
/// registering it is pure cost and pure silence.</para>
///
/// <para>MCP tools are <see cref="McpReach"/>'s, not this one's: they are answered per server, with
/// their own sentence and their own fix, and counting them here would say the same thing twice in
/// different words.</para>
/// </summary>
public static class ToolReach
{
    /// <summary>
    /// What to say about the tools nobody can reach, or null when every registered tool has a role
    /// that names it.
    /// </summary>
    /// <param name="registered">Every tool name the registry holds.</param>
    /// <param name="namedBy">
    /// What each role names. Passed as the lists rather than as a predicate because the question is
    /// about the TEAM: a tool is unreachable only when no list in it has the tool.
    /// </param>
    public static string? Unnamed(
        IEnumerable<string> registered, IEnumerable<IEnumerable<string>> namedBy)
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wildcard = false;

        foreach (var role in namedBy)
            foreach (var tool in role)
            {
                // A role granted "*" carries every tool there is, so nothing is unreachable.
                if (tool == "*")
                    wildcard = true;

                named.Add(tool);
            }

        if (wildcard)
            return null;

        var orphans = registered
            .Where(name => !name.StartsWith(McpReach.Prefix, StringComparison.Ordinal))
            .Where(name => !named.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (orphans.Length == 0)
            return null;

        // The fix spelled exactly, and the consequence first: "configure the roles" is the sentence
        // somebody reads twice and acts on never.
        return $"Tool(s) registered and named by no role: {string.Join(", ", orphans)}. No run can "
             + "use them, whichever role it picks - the model is never told they exist, and will "
             + "work around them or give up. Add them to a role under Settings → AI → Team, "
             + "or stop registering them.";
    }
}
