namespace Enactive.Application;

using Enactive.Core.Context;
using Enactive.Workspace;

/// <summary>Reads what a workspace's machine is like now: the full probe, as EnvironmentProbe.ProbeFullAsync makes it.</summary>
internal delegate Task<(EnvironmentInfo Info, EnvironmentSnapshot Snapshot)> ProbeEnvironment(WorkspaceInfo workspace, CancellationToken ct);

/// <summary>The environment report: what the viewer shows, and the one line the window's status column keeps, when the probe ran.</summary>
internal sealed record EnvironmentReport(string Text, string? OneLine);

/// <summary>
/// The two reports a workspace is shown as a document: its project memory (the Timeline) and the machine it runs on (the
/// Environment). Every host that shows them shows these words - the window's viewer and the service's pages - including
/// what is said when there is no workspace and when the read fails; written once per host, the two said different things
/// about the same folder.
/// </summary>
internal static class WorkspaceReports
{
    /// <summary>What is said when no workspace is chosen.</summary>
    public const string NoWorkspace = "Set a workspace first.";

    /// <summary>The Timeline's title, as the window's viewer has it.</summary>
    public const string TimelineTitle = "Project Memory";

    /// <summary>The Environment's title, as the window's viewer has it.</summary>
    public const string EnvironmentTitle = "Environment";

    /// <summary>
    /// The workspace's project memory: its runs from their summaries and its decisions and notes (ProjectMemory.Render).
    /// A store that cannot be read is said, not thrown: the window started this unobserved, so a failed read showed nothing
    /// at all and looked like a click that did not register.
    /// </summary>
    public static async Task<string> TimelineAsync(string root, CancellationToken ct)
    {
        try
        {
            var workspace = WorkspaceInfo.For(root);
            var runs = await RunStoreFactory.Create(workspace).LoadSummariesAsync(ct);
            var entries = await MemoryStoreFactory.Create(workspace).LoadAllAsync(ct);
            return ProjectMemory.Render(runs, entries, workspace.RootPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Could not read the project memory: " + ex.Message;
        }
    }

    /// <summary>
    /// The machine the workspace is on, probed now: the static facts, then a snapshot. Looking at the environment is a
    /// read, so this only READS the workspace's id (WorkspaceInfo.For). A probe that throws is said as the window says it.
    /// </summary>
    public static async Task<EnvironmentReport> EnvironmentAsync(string? root, ProbeEnvironment probe, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root))
            return new EnvironmentReport(NoWorkspace, null);
        try
        {
            var (info, snapshot) = await probe(WorkspaceInfo.For(root), ct);
            return new EnvironmentReport(info.Summary() + "\n\n" + snapshot.Describe(), info.OneLine());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EnvironmentReport("Probe failed: " + ex.Message, null);
        }
    }
}
