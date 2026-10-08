namespace Enactive.Settings;

using Enactive.Core.Tools;
using Enactive.Tools.Web;

/// <summary>
/// Which of the saved roles may read the web - read by the web pane, which says it next to the switch: the web
/// tools are in no default role, and a tool no role names is offered to no task, so "on" alone would be a promise
/// nothing keeps.
///
/// <para>The rule is the role gate's (<see cref="ToolAllowlist"/>). The pane used to read the lists itself, with
/// case, and so said nobody could read the web while a role naming <c>Fetch_Url</c> was being let through by the
/// engine. No level is asked for, unlike <see cref="MailRoles"/>: reading a page or making a search needs only
/// Observe, so every role that names the tools can call them.</para>
/// </summary>
public static class WebRoles
{
    /// <summary>The roles whose list reaches either web tool.</summary>
    public static IReadOnlyList<WorkerConfig> Reaching(IEnumerable<WorkerConfig> workers)
        => workers.Where(w => ToolAllowlist.Allows(w.Tools, FetchUrlTool.Name) || ToolAllowlist.Allows(w.Tools, WebSearchTool.Name))
            .ToArray();
}
