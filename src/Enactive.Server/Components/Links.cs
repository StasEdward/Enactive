namespace Enactive.Server.Components;

/// <summary>The addresses of the reading pages, built in one place so a folder path is always escaped the same way.</summary>
internal static class Links
{
    /// <summary>
    /// The Runs screen on a workspace, named by its id (WorkspaceIds) - a path does not survive an address - with what an
    /// action said, when one did; with no workspace, the Runs screen on the one used last.
    /// </summary>
    public static string Runs(Guid? workspace, string? said = null)
    {
        var query = new List<string>();
        if (workspace is { } id)
            query.Add($"ws={id}");
        if (!string.IsNullOrEmpty(said))
            query.Add("said=" + Uri.EscapeDataString(said));
        return query.Count == 0 ? "/" : "/?" + string.Join("&", query);
    }

    /// <summary>The old address of a workspace's history, which the service redirects to <see cref="Runs"/>.</summary>
    public static string History(string workspace) => "history?ws=" + Uri.EscapeDataString(workspace);

    /// <summary>A past run on the Runs screen: its workspace by its id (WorkspaceIds), and the run's.</summary>
    public static string PastRun(Guid workspace, Guid runId) => $"history/{workspace}/{runId}";

    /// <summary>
    /// The Templates screen on a workspace, named by its id (WorkspaceIds) - with none, the one used last - opened on a
    /// template when <paramref name="template"/> names one, with what an action said, when one did.
    /// </summary>
    public static string Templates(Guid? workspace, string? template = null, string? said = null)
    {
        var query = new List<string>();
        if (workspace is { } id)
            query.Add($"ws={id}");
        if (!string.IsNullOrEmpty(template))
            query.Add("t=" + Uri.EscapeDataString(template));
        if (!string.IsNullOrEmpty(said))
            query.Add("said=" + Uri.EscapeDataString(said));
        return query.Count == 0 ? "templates" : "templates?" + string.Join("&", query);
    }

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

    /// <summary>
    /// The Schedules screen on a workspace, named by its id (WorkspaceIds) - with none, the one used last - opened on a
    /// schedule when <paramref name="schedule"/> names one.
    /// </summary>
    public static string Schedules(Guid? workspace, Guid? schedule = null)
    {
        var path = schedule is { } id ? $"schedules/{id}" : "schedules";
        return workspace is { } ws ? $"{path}?ws={ws}" : path;
    }

    /// <summary>One run's set of proposed changes: its workspace by its id (WorkspaceIds), the set by the run's id.</summary>
    public static string Proposals(Guid workspace, string set) => $"proposals/{workspace}/{Uri.EscapeDataString(set)}";
}
