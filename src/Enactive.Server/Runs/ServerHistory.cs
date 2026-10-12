namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Agents;
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
internal sealed class ServerHistory(UserStore users, ServerEngine engine, IRunControl runs, WorkspaceRoots roots)
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

    /// <summary>
    /// "Delete these N": the runs <paramref name="filter"/> shows, and only those - picked by the window's own rule
    /// (RunHousekeeping.Where over the history as it is listed), never by a second one of the page's: two rules behind one
    /// button could disagree about a row, and the row they disagreed on would be gone. The page asks first, with the count
    /// (RunHistoryService.DeleteMany). What tells the person, and how many went.
    /// </summary>
    public async Task<(string Said, int Deleted)> DeleteShownAsync(
        ClaimsPrincipal user, string root, RunFilter filter, DateTimeOffset now, CancellationToken ct)
    {
        if (Operating(user, root) is not { } known)
            return (NotOperating(user), 0);
        var who = users.Find(user.Identity?.Name ?? "")!.Name;

        var list = await _history.LoadAsync(known.RootPath, ct);
        if (list.Problem is { } unreadable)
            return (unreadable, 0);
        var shown = RunHousekeeping.Where(list.Runs, filter, now);
        if (shown.Count == 0)
            return ("There are no runs to delete.", 0);

        var refused = await _history.DeleteAsync(known.RootPath, shown, ct);
        // Some could not be deleted, and the bulk delete does not say which: the history is read again, so only the runs
        // really gone are audited as deleted - an audit naming a run that is still there is a record that lies.
        var gone = refused is null
            ? shown
            : shown.ExceptBy((await _history.LoadAsync(known.RootPath, ct)).Runs.Select(r => r.RunId), r => r.RunId).ToList();
        foreach (var run in gone)
            users.Audit(who, "run deleted", $"{known.RootPath}: {RunTitle.For(run)}");
        return (refused ?? $"{gone.Count} run{(gone.Count == 1 ? "" : "s")} deleted; the files they wrote are where they were.", gone.Count);
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

    // ── a workspace's run settings (Operators) ──────────────────────────────
    // The slider, the worker box and the staging switch of the window, saved as the window saves them: against the
    // folder, where the next run in it - from the browser or the console - reads them (EngineExecutor).

    /// <summary>
    /// The workers a run in a workspace can be given, by id and the name the person knows them by - what the window's
    /// worker box lists: the engine's workers, or - with no engine to run on yet - the settings' own. Empty when there
    /// are none, and then the workspace's default worker is all there is to pick.
    /// </summary>
    public IReadOnlyList<(string Id, string Name)> Workers()
        => engine.Current is { } composed
            ? composed.Workers.All.Select(w => (w.Id, w.Role)).ToList()
            : engine.Settings?.Workers.Select(w => (w.Id, w.Role)).ToList() ?? [];

    /// <summary>
    /// Saves a workspace's level, worker and staging; null when saved, else why not. A blank <paramref name="workerId"/> is
    /// the workspace's default worker.
    /// </summary>
    public string? SaveSettings(ClaimsPrincipal user, string root, int autonomy, string? workerId, bool stageChanges)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return "Your session has ended. Sign in again.";
        if (account.Role < Role.Operator)
            return "Your role does not include changing a workspace's settings.";

        // Before the registry is asked: its SaveSettings lists any folder it is given, so a post naming a path on the
        // server's disks would have made it one of the workspaces the service shows (and reads) to everybody.
        if (engine.Workspaces.Find(root) is not { } known)
            return "The service does not know that folder as a workspace.";

        // A level the slider has no position for is refused, not clamped: the window cannot send one, so a page that does
        // is wrong, and saving "the top" for a 9 would be a run with more freedom than anyone asked for.
        if (autonomy < 0 || autonomy > AutonomyTiers.Highest)
            return $"The level must be from 0 to {AutonomyTiers.Highest}.";

        // The worker is kept by id. One the settings no longer have would be saved and then quietly replaced by the
        // default worker on every run, which reads as a choice that was ignored. A role name resolves to its id as a
        // run resolves it (ComposedEngine.WorkerIdFor): the window saved role names until 2026-10-08, and a folder still
        // holding one could not otherwise be saved again from a page that offers its stored worker.
        string? worker = null;
        if (!string.IsNullOrWhiteSpace(workerId))
        {
            worker = WorkerNames.Resolve(Workers(), workerId);
            if (worker is null)
                return $"There is no worker “{workerId.Trim()}” in the settings.";
        }

        engine.Workspaces.SaveSettings(known.RootPath, autonomy, worker, stageChanges);
        users.Audit(account.Name, "workspace settings changed",
            $"{known.RootPath}: {AutonomyTiers.Names[autonomy]}, worker {worker ?? "default"}, staging {(stageChanges ? "on" : "off")}");
        return null;
    }

    // ── adding a workspace (Operators) ──────────────────────────────────────

    /// <summary>The roots, or the folders below one of them, for an Operator choosing a workspace; nothing for anybody else.</summary>
    public (IReadOnlyList<string> Folders, string? Problem) Browse(ClaimsPrincipal user, string? folder)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator })
            return ([], "Your role does not include adding workspaces.");
        if (string.IsNullOrWhiteSpace(folder))
            return (roots.All, roots.All.Count == 0 ? roots.Refusal("") : null);
        return roots.Refusal(folder) is { } refused ? ([], refused) : (roots.Below(folder), null);
    }

    /// <summary>Makes a folder under the roots one of the service's workspaces - where its runs, history and inbox are.</summary>
    public string AddWorkspace(ClaimsPrincipal user, string folder)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator } account)
            return "Your role does not include adding workspaces.";
        if (roots.Refusal(folder) is { } refused)
            return refused;
        if (!Directory.Exists(folder))
            return $"No such folder: {folder}.";
        var entry = engine.Workspaces.Touch(folder);
        users.Audit(account.Name, "workspace added", entry.RootPath);
        return $"{entry.Name} is a workspace of the service now.";
    }
}

