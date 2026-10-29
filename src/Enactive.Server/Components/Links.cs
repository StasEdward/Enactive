namespace Enactive.Server.Components;

using Microsoft.AspNetCore.Components;

/// <summary>The addresses of the reading pages, built in one place so a folder path is always escaped the same way.</summary>
internal static class Links
{
    /// <summary>
    /// The Runs screen on a workspace, named by its id (WorkspaceIds) - a path does not survive an address; with no
    /// workspace, the Runs screen on the one used last.
    /// </summary>
    public static string Runs(Guid? workspace) => workspace is { } id ? $"/?ws={id}" : "/";

    /// <summary>
    /// A navigation that carries what an action said to the page it leads to - in the navigation's own state
    /// (<see cref="NavigationManager.HistoryEntryState"/>, read back by <see cref="SaidOn"/>), never in the address. In the
    /// address ("?said=") any link could have the service say any words in its own voice - "Your session has ended; sign
    /// in at …" - and the words outlived signing in, carried in its return address. Only the service's own pages can set
    /// the state.
    /// </summary>
    public static NavigationOptions Saying(string? said) => new() { HistoryEntryState = string.IsNullOrEmpty(said) ? null : said };

    /// <summary>What the action that led to this page said (<see cref="Saying"/>), or null.</summary>
    public static string? SaidOn(NavigationManager navigation) => navigation.HistoryEntryState;

    /// <summary>A past run on the Runs screen: its workspace by its id (WorkspaceIds), and the run's.</summary>
    public static string PastRun(Guid workspace, Guid runId) => $"history/{workspace}/{runId}";

    /// <summary>
    /// The Templates screen on a workspace, named by its id (WorkspaceIds) - with none, the one used last - opened on a
    /// template when <paramref name="template"/> names one.
    /// </summary>
    public static string Templates(Guid? workspace, string? template = null)
    {
        var query = new List<string>();
        if (workspace is { } id)
            query.Add($"ws={id}");
        if (!string.IsNullOrEmpty(template))
            query.Add("t=" + Uri.EscapeDataString(template));
        return query.Count == 0 ? "templates" : "templates?" + string.Join("&", query);
    }

    /// <summary>A workspace's Checks screen (Plan Phase 3.5), by its id (WorkspaceIds); with none, the one used last.</summary>
    public static string Checks(Guid? workspace) => workspace is { } id ? $"checks?ws={id}" : "checks";

    /// <summary>A workspace's Lessons screen (Plan Phase 5.2), by its id; with none, the one used last.</summary>
    public static string Lessons(Guid? workspace) => workspace is { } id ? $"lessons?ws={id}" : "lessons";

    /// <summary>A workspace's Checks screen with a new check's form filled in from a lesson (Plan Phase 5.4).</summary>
    public static string CheckFromLesson(Guid workspace, Guid lesson) => $"checks?ws={workspace}&lesson={lesson:N}";

    /// <summary>
    /// The template editor on a template of a workspace: its own (<paramref name="copy"/> false) or a copy of it.
    /// </summary>
    public static string Template(Guid workspace, string id, bool copy = false)
        => $"templates/{Uri.EscapeDataString(id)}?ws={workspace}" + (copy ? "&copy=true" : "");

    /// <summary>
    /// The template editor on a new template. "New" can never be a template's id - an id starts with a lowercase letter
    /// (TemplateValidator) - so a template called "new" keeps its own address, templates/new.
    /// </summary>
    public const string NewTemplateId = "New";

    public static string NewTemplate(Guid workspace) => $"templates/{NewTemplateId}?ws={workspace}";

    /// <summary>Settings › Templates' address (SettingsSection).</summary>
    public const string TemplatesSettingsSlug = "ai-templates";

    /// <summary>
    /// The template editor over Settings › Templates (TemplatesSection) rather than over the library: on a template, a copy
    /// of it, or a new one (<see cref="NewTemplateId"/>). Closing it goes back to Settings › Templates - opened from there,
    /// the library's address took the settings' menu away and left the person on the Templates screen.
    ///
    /// <para>The template in the path, as the library's editor has it, not in the query: a section reading ?edit= drew the
    /// editor while Blazor was still telling the page's components of the new query, the editor's own query values joined
    /// that list as it was walked, and the navigation failed ("Collection was modified") - the editor never opened.</para>
    /// </summary>
    public static string SettingsTemplate(Guid workspace, string id, bool copy = false)
        => $"{Settings(TemplatesSettingsSlug)}/{Uri.EscapeDataString(id)}?ws={workspace}" + (copy ? "&copy=true" : "");

    /// <summary>
    /// The Schedules screen on a workspace, named by its id (WorkspaceIds) - with none, the one used last - opened on a
    /// schedule when <paramref name="schedule"/> names one.
    /// </summary>
    public static string Schedules(Guid? workspace, Guid? schedule = null)
    {
        var path = schedule is { } id ? $"schedules/{id}" : "schedules";
        return workspace is { } ws ? $"{path}?ws={ws}" : path;
    }

    /// <summary>The Timeline - a workspace's project memory - on a workspace by its id (WorkspaceIds); none, the one used last.</summary>
    public static string Timeline(Guid? workspace) => workspace is { } ws ? $"timeline?ws={ws}" : "timeline";

    /// <summary>The Environment - the service's machine as a workspace sees it - on a workspace by its id; none, the one used last.</summary>
    public static string Environment(Guid? workspace) => workspace is { } ws ? $"environment?ws={ws}" : "environment";

    /// <summary>The settings, on a section by its address (Settings/SettingsSection); none, the first.</summary>
    public static string Settings(string? section = null) => section is { Length: > 0 } ? $"settings/{section}" : "settings";

    /// <summary>One run's set of proposed changes: its workspace by its id (WorkspaceIds), the set by the run's id.</summary>
    public static string Proposals(Guid workspace, string set) => $"proposals/{workspace}/{Uri.EscapeDataString(set)}";
}
