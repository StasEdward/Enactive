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

    public static string Library(string workspace) => "library?ws=" + Uri.EscapeDataString(workspace);

    public static string Template(string workspace, string? id)
        => $"library/template?ws={Uri.EscapeDataString(workspace)}" + (id is null ? "" : "&id=" + Uri.EscapeDataString(id));

    public static string Schedule(string workspace, Guid? id)
        => $"library/schedule?ws={Uri.EscapeDataString(workspace)}" + (id is null ? "" : $"&id={id}");

    public static string Proposals(string workspace, string set)
        => $"proposals/view?ws={Uri.EscapeDataString(workspace)}&set={Uri.EscapeDataString(set)}";
}
