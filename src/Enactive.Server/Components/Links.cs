namespace Enactive.Server.Components;

/// <summary>The addresses of the reading pages, built in one place so a folder path is always escaped the same way.</summary>
internal static class Links
{
    public static string History(string workspace) => "history?ws=" + Uri.EscapeDataString(workspace);

    public static string PastRun(string workspace, Guid runId) => $"history/run?ws={Uri.EscapeDataString(workspace)}&id={runId}";

    public static string Confirm(string workspace, string what, Guid id, string? path = null)
        => $"history/confirm?ws={Uri.EscapeDataString(workspace)}&what={what}&id={id}"
           + (path is null ? "" : "&path=" + Uri.EscapeDataString(path));

    public static string File(string workspace, string path)
        => $"file?ws={Uri.EscapeDataString(workspace)}&path={Uri.EscapeDataString(path)}";
}
