namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Server.Accounts;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>A finished run, as the service's run page shows it: its head, its cards, its timeline and its tiles.</summary>
internal sealed record PastRun(
    RunDigest Digest,
    IReadOnlyList<FeedCard> Cards,
    IReadOnlyList<RunEventViewModel> Timeline,
    IReadOnlyList<RoutingRow> Routing,
    IReadOnlyList<ModelWorkRow> Work,
    IReadOnlyList<string> Decisions,
    bool FromTemplate);

/// <summary>
/// Reading the history of the service's workspaces (server plan 1.6), for anybody signed in.
///
/// <para><b>Only the workspaces the service knows</b> - the folders it has run in, its workspace list. A Viewer reads
/// runs; a Viewer naming any folder on the server's disks would read whatever history and files sat under it, which
/// is reading the server's disks. Checked here, with the role, at each call (ADR 0002 §5) - not by the pages.</para>
/// </summary>
internal sealed class ServerHistory(UserStore users, ServerEngine engine, IRunControl runs)
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
            record.Decisions,
            Core.Templates.ResolvedTaskSpec.Parse(record.Spec) is not null), null);
    }

    /// <summary>A file of a known workspace, as text, within the limits a page has (WorkspaceFiles).</summary>
    public (string? Text, string? Problem) File(ClaimsPrincipal user, string root, string relative)
        => Workspace(user, root) is { } known ? WorkspaceFiles.Read(known.RootPath, relative) : (null, Unknown(user));

    private bool CanRead(ClaimsPrincipal user) => ServerAuth.StillValid(users, user) is { Role: >= Role.Viewer };

    private string Unknown(ClaimsPrincipal user)
        => CanRead(user) ? "The service does not know that folder as a workspace." : "Your session has ended. Sign in again.";

    // ── acting on the history (Operators) ───────────────────────────────────
    // What the window does with a past run, from the browser - with the window's own questions (RunHistoryService), asked
    // on a page before anything that cannot be taken back, and each action audited with who did it.

    /// <summary>The things a page can ask to have done to a workspace's history.</summary>
    public static class Act
    {
        public const string Retry = "retry";
        public const string RunAgain = "run-again";
        public const string DeleteRun = "delete-run";
        public const string Forget = "forget";
        public const string DeleteFile = "delete-file";
    }

    /// <summary>
    /// The question to ask before <paramref name="act"/> - the words the window asks it in - or null and why it cannot be
    /// asked: not an Operator, not a known workspace, nothing there to act on.
    /// </summary>
    public async Task<(Application.Question? Question, string? Problem)> AskAsync(
        ClaimsPrincipal user, string root, string act, Guid id, string? path, CancellationToken ct)
    {
        if (Operating(user, root) is not { } known)
            return (null, NotOperating(user));
        switch (act)
        {
            case Act.DeleteRun:
                var list = await _history.LoadAsync(known.RootPath, ct);
                return list.Runs.FirstOrDefault(r => r.RunId == id) is { } run
                    ? (RunHistoryService.DeleteOne(run), null)
                    : (null, "There is no such run in this workspace.");
            case Act.Forget:
                return await CheckpointAsync(known.RootPath, id, ct) is { } checkpoint
                    ? (RunHistoryService.ForgetUnfinished(known.RootPath, checkpoint), null)
                    : (null, "There is no such unfinished run in this workspace.");
            case Act.DeleteFile:
                return RunHistoryService.DeleteFile(known.RootPath, path ?? "") is { } question
                    ? (question, null)
                    : (null, "That file is not there any more, or not in this workspace.");
            default:
                return (null, "There is nothing to ask before that.");
        }
    }

    /// <summary>
    /// Does it. <paramref name="choice"/> is "confirm" or "alternative" (forgetting an unfinished run: put its files back,
    /// or keep them). What to tell the person, and the run started, for Retry and Run again.
    /// </summary>
    public async Task<(string Said, Guid? Run)> DoAsync(
        ClaimsPrincipal user, string root, string act, Guid id, string? path, string? choice, CancellationToken ct)
    {
        if (Operating(user, root) is not { } known)
            return (NotOperating(user), null);
        var who = users.Find(user.Identity?.Name ?? "")!.Name;

        switch (act)
        {
            case Act.Retry:
            case Act.RunAgain:
            {
                var (past, problem) = await OpenAsync(user, known.RootPath, id, ct);
                var record = past is null ? null : await RecordAsync(known.RootPath, id, ct);
                if (record is null)
                    return (problem ?? "There is no such run in this workspace.", null);
                Rerun? again;
                if (act == Act.Retry)
                    again = RunHistoryService.Retry(record);
                else
                {
                    // Re-resolved against the folder's level as it is now, as the window resolves it against its slider.
                    var policy = EngineComposition.PolicyFor(engine.Settings ?? new AppSettings(), known.Autonomy);
                    (again, problem) = RunHistoryService.RunAgain(known.RootPath, record, policy);
                    if (problem is not null)
                        return (problem, null);
                }
                if (again is null)
                    return ("There is nothing to run again in that run.", null);
                var (run, refusal) = runs.Again(user, known.RootPath, again, act == Act.Retry ? "run tried again" : "template run again");
                return run is null ? (refusal ?? "Not started.", null) : ("", run.Id);
            }
            case Act.DeleteRun:
            {
                var list = await _history.LoadAsync(known.RootPath, ct);
                if (list.Runs.FirstOrDefault(r => r.RunId == id) is not { } run)
                    return ("There is no such run in this workspace.", null);
                if (await _history.DeleteAsync(known.RootPath, run, ct) is { } problem)
                    return (problem, null);
                users.Audit(who, "run deleted", $"{known.RootPath}: {RunTitle.For(run)}");
                return ("The run's record is deleted; the files it wrote are where they were.", null);
            }
            case Act.Forget:
            {
                if (await CheckpointAsync(known.RootPath, id, ct) is not { } checkpoint)
                    return ("There is no such unfinished run in this workspace.", null);
                var putBack = choice == "confirm" && RunHistoryService.ForgetUnfinished(known.RootPath, checkpoint).Alternative is not null;
                var (kept, problem) = await _history.ForgetUnfinishedAsync(known.RootPath, checkpoint, putBack, ct);
                if (problem is not null)
                    return (problem, null);
                users.Audit(who, putBack ? "unfinished run forgotten, files put back" : "unfinished run forgotten",
                    $"{known.RootPath}: {RunTitle.OneLine(checkpoint.Title)}");
                return (kept ?? (putBack ? "Forgotten, and its files are put back." : "Forgotten."), null);
            }
            case Act.DeleteFile:
            {
                var (status, _) = RunHistoryService.DeleteFileNow(known.RootPath, path ?? "");
                users.Audit(who, "file deleted", $"{known.RootPath}: {path} ({status})");
                return ($"{path}: {status}.", null);
            }
            default:
                return ("That is not something the history does.", null);
        }
    }

    private async Task<Core.History.RunRecord?> RecordAsync(string root, Guid id, CancellationToken ct)
    {
        var list = await _history.LoadAsync(root, ct);
        return list.Runs.FirstOrDefault(r => r.RunId == id) is { } summary
            ? (await _history.OpenAsync(root, summary, ct)).Record
            : null;
    }

    private static async Task<Core.History.RunCheckpoint?> CheckpointAsync(string root, Guid runId, CancellationToken ct)
        => (await new JsonCheckpointStore(WorkspaceInfo.For(root)).LoadAllAsync(ct)).FirstOrDefault(c => c.RunId == runId);

    /// <summary>A known workspace, for an Operator whose account still speaks for them; null otherwise.</summary>
    private WorkspaceEntry? Operating(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Operator } ? engine.Workspaces.Find(root) : null;

    private string NotOperating(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is not { } account ? "Your session has ended. Sign in again."
            : account.Role < Role.Operator ? "Your role does not include acting on runs."
            : "The service does not know that folder as a workspace.";
}

