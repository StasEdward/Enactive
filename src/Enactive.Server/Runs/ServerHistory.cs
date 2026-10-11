namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.History;
using Enactive.Server.Accounts;
using Enactive.Workspace;

/// <summary>A finished run, as the service's run page shows it: its head, its cards, its timeline and its tiles.</summary>
internal sealed record PastRun(
    RunDigest Digest,
    IReadOnlyList<FeedCard> Cards,
    IReadOnlyList<RunEventViewModel> Timeline,
    IReadOnlyList<RoutingRow> Routing,
    IReadOnlyList<ModelWorkRow> Work,
    IReadOnlyList<string> Decisions);

/// <summary>
/// Reading the history of the service's workspaces (server plan 1.6), for anybody signed in.
///
/// <para><b>Only the workspaces the service knows</b> - the folders it has run in, its workspace list. A Viewer reads
/// runs; a Viewer naming any folder on the server's disks would read whatever history and files sat under it, which
/// is reading the server's disks. Checked here, with the role, at each call (ADR 0002 §5) - not by the pages.</para>
/// </summary>
internal sealed class ServerHistory(UserStore users, ServerEngine engine)
{
    private readonly RunHistoryService _history = new(() => 0);

    /// <summary>The workspaces the service has run in, newest first.</summary>
    public IReadOnlyList<WorkspaceEntry> Workspaces(ClaimsPrincipal user)
        => CanRead(user) ? engine.Workspaces.Entries.OrderByDescending(e => e.LastOpenedAt).ToList() : [];

    /// <summary>The workspace as the service knows it, or null - for somebody who may read, a folder it knows.</summary>
    public WorkspaceEntry? Workspace(ClaimsPrincipal user, string root)
        => CanRead(user) ? engine.Workspaces.Find(root) : null;

    public async Task<(HistoryList? List, string? Problem)> ListAsync(ClaimsPrincipal user, string root, CancellationToken ct)
    {
        if (Workspace(user, root) is not { } known)
            return (null, Unknown(user));
        return (await _history.LoadAsync(known.RootPath, ct), null);
    }

    public async Task<(PastRun? Run, string? Problem)> OpenAsync(ClaimsPrincipal user, string root, Guid runId, CancellationToken ct)
    {
        if (Workspace(user, root) is not { } known)
            return (null, Unknown(user));

        var list = await _history.LoadAsync(known.RootPath, ct);
        if (list.Runs.FirstOrDefault(r => r.RunId == runId) is not { } summary)
            return (null, "There is no such run in this workspace.");
        var (record, problem) = await _history.OpenAsync(known.RootPath, summary, ct);
        if (record is null)
            return (null, problem);

        // The same folds and tiles the window shows for a past run, off what was recorded.
        var attempts = RunHistory.AttemptsOf(list.Runs, record);
        var settings = engine.Settings;
        return (new PastRun(
            RunDigest.Of(record, attempts),
            RunFeed.Replay(record).Cards,
            RunTimeline.Fold(record),
            RunRouting.From(record).Rows.ToList(),
            ModelWorkSplit.From(record, id => settings is null ? ModelReach.Unknown : ModelReaches.Of(settings, id)).Rows.ToList(),
            record.Decisions), null);
    }

    /// <summary>A file of a known workspace, as text, within the limits a page has (WorkspaceFiles).</summary>
    public (string? Text, string? Problem) File(ClaimsPrincipal user, string root, string relative)
        => Workspace(user, root) is { } known ? WorkspaceFiles.Read(known.RootPath, relative) : (null, Unknown(user));

    private bool CanRead(ClaimsPrincipal user) => ServerAuth.StillValid(users, user) is { Role: >= Role.Viewer };

    private string Unknown(ClaimsPrincipal user)
        => CanRead(user) ? "The service does not know that folder as a workspace." : "Your session has ended. Sign in again.";
}
