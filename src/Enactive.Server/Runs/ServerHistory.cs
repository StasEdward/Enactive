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

/// <summary>
/// A finished run, as the Runs screen shows it over the live one: its head, its cards, its timeline and its tiles - the
/// request as it was typed (<see cref="Request"/>, null for a run recorded before it was kept), and whether Run again is
/// offered (RunAgainOffer: only while its template is still in the library).
/// </summary>
internal sealed record PastRun(
    RunDigest Digest,
    IReadOnlyList<FeedCard> Cards,
    IReadOnlyList<RunEventViewModel> Timeline,
    IReadOnlyList<RoutingRow> Routing,
    IReadOnlyList<ModelWorkRow> Work,
    IReadOnlyList<string> Decisions,
    string? Request,
    RunAgainOffer RunAgain);

/// <summary>
/// Reading the history of the service's workspaces (server plan 1.6), for anybody signed in.
///
/// <para><b>Only the workspaces the service knows, under its roots</b> (KnownWorkspaces) - the folders it has run in, its
/// workspace list. A Viewer reads runs; a Viewer naming any folder on the server's disks would read whatever history and
/// files sat under it, which is reading the server's disks. Checked here, with the role, at each call (ADR 0002 §5) - not
/// by the pages.</para>
/// </summary>
internal sealed class ServerHistory(UserStore users, ServerEngine engine, IRunControl runs, KnownWorkspaces workspaces)
{
    private readonly RunHistoryService _history = new(() => 0);

    /// <summary>The workspaces the service has run in, newest first.</summary>
    public IReadOnlyList<WorkspaceEntry> Workspaces(ClaimsPrincipal user)
        => CanRead(user) ? workspaces.All.OrderByDescending(e => e.LastOpenedAt).ToList() : [];

    /// <summary>The workspace as the service knows it, or null - for somebody who may read, a folder it knows.</summary>
    public WorkspaceEntry? Workspace(ClaimsPrincipal user, string root)
        => CanRead(user) ? workspaces.Find(root) : null;

    public async Task<(HistoryList? List, string? Problem)> ListAsync(ClaimsPrincipal user, string root, CancellationToken ct)
    {
        if (Workspace(user, root) is not { } known)
            return (null, Unknown(user));
        return (await LoadAsync(known.RootPath, ct), null);
    }

    public async Task<(PastRun? Run, string? Problem)> OpenAsync(ClaimsPrincipal user, string root, Guid runId, CancellationToken ct)
    {
        if (Workspace(user, root) is not { } known)
            return (null, Unknown(user));

        var list = await LoadAsync(known.RootPath, ct);
        if (list.Runs.FirstOrDefault(r => r.RunId == runId) is not { } summary)
            return (null, "There is no such run in this workspace.");
        var (record, problem) = await _history.OpenAsync(known.RootPath, summary, ct);
        if (record is null)
            return (null, problem);

        // The same folds and tiles the window shows for a past run, off what was recorded.
        var attempts = RunHistory.AttemptsOf(list.Runs, record);
        var settings = engine.Settings;
        // Whether the template is still there is a fact about now, read as the window reads it (its library in the folder).
        var spec = Core.Templates.ResolvedTaskSpec.Parse(record.Spec);
        return (new PastRun(
            RunDigest.Of(record, attempts),
            RunFeed.Replay(record).Cards,
            RunTimeline.Fold(record),
            RunRouting.From(record).Rows.ToList(),
            ModelWorkSplit.From(record, id => settings is null ? ModelReach.Unknown : ModelReaches.Of(settings, id)).Rows.ToList(),
            record.Decisions,
            RunHistory.RequestOf(record),
            RunAgainOffer.Of(spec, spec is null ? null : new TemplateStore(known.RootPath).Find(spec.TemplateId))), null);
    }

    /// <summary>A file of a known workspace, as text, within the limits a page has (WorkspaceFiles).</summary>
    public (string? Text, string? Problem) File(ClaimsPrincipal user, string root, string relative)
        => Workspace(user, root) is { } known ? WorkspaceFiles.Read(known.RootPath, relative) : (null, Unknown(user));

    private bool CanRead(ClaimsPrincipal user) => ServerAuth.StillValid(users, user) is { Role: >= Role.Viewer };

    private string Unknown(ClaimsPrincipal user)
        => CanRead(user) ? "The service does not know that folder as a workspace." : "Your session has ended. Sign in again.";

    // ── acting on the history (Operators) ───────────────────────────────────
    // What the window does with a past run, from the browser - with the window's own questions (RunHistoryService), asked
    // in a dialog on the screen before anything that cannot be taken back, and each action audited with who did it.

    /// <summary>A file's card, when the file it names is not there to ask about deleting: the window's words.</summary>
    public const string AlreadyGone = "already gone";

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
                var list = await LoadAsync(known.RootPath, ct);
                return list.Runs.FirstOrDefault(r => r.RunId == id) is { } run
                    ? (RunHistoryService.DeleteOne(run), null)
                    : (null, "There is no such run in this workspace.");
            case Act.Forget:
                return await CheckpointAsync(known.RootPath, id, ct) is { } checkpoint
                    ? (RunHistoryService.ForgetUnfinished(known.RootPath, checkpoint), null)
                    : (null, "There is no such unfinished run in this workspace.");
            case Act.DeleteFile:
                // The window's words for a file there is nothing to ask about, on the file's card (DeletePastArtifactAsync).
                return RunHistoryService.DeleteFile(known.RootPath, path ?? "") is { } question
                    ? (question, null)
                    : (null, AlreadyGone);
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
                var list = await LoadAsync(known.RootPath, ct);
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
            // Not Act.DeleteFile: a file is deleted through DeleteFile alone, which also says whether its card is done with.
            // A second way in that nothing called was a second path to keep checking the same as the first.
            default:
                return ("That is not something the history does.", null);
        }
    }

    /// <summary>
    /// Deletes a file of a known workspace - what a run wrote, from its card - and says what became of it in the words the
    /// window's card shows (RunHistoryService.DeleteFileNow), and whether the card is done with: an error leaves it to try
    /// again, as the window's does. A refusal (not an Operator, not a known workspace) is not done with either. The card
    /// asks first (<see cref="AskAsync"/>, <see cref="Act.DeleteFile"/>).
    /// </summary>
    public (string Status, bool Done) DeleteFile(ClaimsPrincipal user, string root, string path)
    {
        if (Operating(user, root) is not { } known)
            return (NotOperating(user), false);
        var (status, done) = RunHistoryService.DeleteFileNow(known.RootPath, path);
        users.Audit(users.Find(user.Identity?.Name ?? "")!.Name, "file deleted", $"{known.RootPath}: {path} ({status})");
        return (status, done);
    }

    /// <summary>
    /// "Delete these N": the runs the page showed - <paramref name="shown"/>, picked there by the window's rule
    /// (RunHousekeeping.Where) - and that are still in the history. Not the filter chosen again at the click: a run that
    /// ended between showing and confirming joined the filter and was deleted unseen, by a question whose count did not
    /// include it. The page asks first, with the count (RunHistoryService.DeleteMany). What tells the person, and how
    /// many went.
    /// </summary>
    public async Task<(string Said, int Deleted)> DeleteShownAsync(
        ClaimsPrincipal user, string root, IReadOnlyCollection<Guid> shown, CancellationToken ct)
    {
        if (Operating(user, root) is not { } known)
            return (NotOperating(user), 0);
        var who = users.Find(user.Identity?.Name ?? "")!.Name;

        var list = await LoadAsync(known.RootPath, ct);
        if (list.Problem is { } unreadable)
            return (unreadable, 0);
        var ids = shown.ToHashSet();
        var doomed = list.Runs.Where(r => ids.Contains(r.RunId)).ToList();
        if (doomed.Count == 0)
            return ("There are no runs to delete.", 0);

        var refused = await _history.DeleteAsync(known.RootPath, doomed, ct);
        // Some could not be deleted, and the bulk delete does not say which: the history is read again, so only the runs
        // really gone are audited as deleted - an audit naming a run that is still there is a record that lies.
        var gone = refused is null ? doomed : ConfirmedGone(doomed, await LoadAsync(known.RootPath, ct));
        if (gone is null)
            return (refused + " The history could not be read again to tell which.", 0);
        foreach (var run in gone)
            users.Audit(who, "run deleted", $"{known.RootPath}: {RunTitle.For(run)}");
        return (refused ?? $"{gone.Count} run{(gone.Count == 1 ? "" : "s")} deleted; the files they wrote are where they were.", gone.Count);
    }

    /// <summary>
    /// Which of <paramref name="deleted"/> the history read <paramref name="after"/> no longer has - or null when it could not
    /// be read. An unreadable history lists no runs, and every run asked for read as gone and was audited as deleted.
    /// </summary>
    internal static IReadOnlyList<RunSummary>? ConfirmedGone(IReadOnlyList<RunSummary> deleted, HistoryList after)
        => after.Problem is not null ? null : deleted.ExceptBy(after.Runs.Select(r => r.RunId), r => r.RunId).ToList();

    private async Task<Core.History.RunRecord?> RecordAsync(string root, Guid id, CancellationToken ct)
    {
        var list = await LoadAsync(root, ct);
        return list.Runs.FirstOrDefault(r => r.RunId == id) is { } summary
            ? (await _history.OpenAsync(root, summary, ct)).Record
            : null;
    }

    private static async Task<Core.History.RunCheckpoint?> CheckpointAsync(string root, Guid runId, CancellationToken ct)
        => Directory.Exists(root)
            ? (await new JsonCheckpointStore(WorkspaceInfo.For(root)).LoadAllAsync(ct)).FirstOrDefault(c => c.RunId == runId)
            : null;

    /// <summary>
    /// A known workspace's history - none when its folder is gone. Reading it anyway made the stores' folders under the
    /// workspace's root, and so the workspace itself again, empty: a folder somebody had moved or deleted came back on
    /// the server's disk because a page looked at it (found by WorkspaceColumnTests), and the Runs screen then offered to
    /// run in it.
    /// </summary>
    private Task<HistoryList> LoadAsync(string root, CancellationToken ct)
        => Directory.Exists(root) ? _history.LoadAsync(root, ct) : Task.FromResult(new HistoryList([], [], null));

    /// <summary>A known workspace, for an Operator whose account still speaks for them; null otherwise.</summary>
    private WorkspaceEntry? Operating(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Operator } ? workspaces.Find(root) : null;

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
    public IReadOnlyList<(string Id, string Name)> Workers() => engine.Workers();

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
        if (workspaces.Find(root) is not { } known)
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

    // ── renaming and forgetting a workspace (Operators) ─────────────────────

    /// <summary>
    /// The longest name kept. The window has no limit, its person typing for themselves; here any Operator names a
    /// workspace for everybody, and a page can send a name of any length - which every switcher, card and mail would then
    /// carry.
    /// </summary>
    public const int NameLimit = 200;

    /// <summary>
    /// Gives a workspace a name of its own, as the window's Rename: what the service calls it - the folder is not touched.
    /// A blank name is the folder's own again (WorkspaceRegistry.Rename). Audited.
    /// </summary>
    public string RenameWorkspace(ClaimsPrincipal user, string root, string name)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator } account)
            return "Your role does not include renaming workspaces.";
        // Before the registry is asked, as SaveSettings: a folder outside the roots, or one the service does not list, is
        // not a workspace of the service to rename.
        if (workspaces.Find(root) is not { } known)
            return "The service does not know that folder as a workspace.";
        if (name.Trim().Length > NameLimit)
            return $"A name is at most {NameLimit} characters.";

        engine.Workspaces.Rename(known.RootPath, name);
        var now = engine.Workspaces.Find(known.RootPath)?.Name ?? known.Name;
        users.Audit(account.Name, "workspace renamed", $"{known.RootPath}: \u201c{known.Name}\u201d to \u201c{now}\u201d");
        return $"\u201c{known.Name}\u201d is called \u201c{now}\u201d now.";
    }

    /// <summary>
    /// Forgets a workspace, as the window's Forget: the service's list loses it - its name and how runs are set up in it -
    /// and nothing on disk is touched, the folder and its run history included. Added again under the roots, it is back
    /// with its history. Audited.
    ///
    /// <para>What goes on there is said too, and audited: the runs going there finish where they are, those waiting in the
    /// line do not start (RunControl.NotAWorkspace) and its schedules stop until the folder is added again
    /// (ServerScheduler). On the service another Operator's runs and schedules may be there, and a forget that said only
    /// "forgotten" stopped them with nobody told.</para>
    /// </summary>
    public string ForgetWorkspace(ClaimsPrincipal user, string root)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator } account)
            return "Your role does not include forgetting workspaces.";
        if (workspaces.Find(root) is not { } known)
            return "The service does not know that folder as a workspace.";

        var there = runs.List(user).Where(r => !r.Ended.IsCompleted && SameFolder(r.Workspace, known.RootPath)).ToList();
        var going = there.Count(r => r.RunningSince is not null);
        var waiting = there.Count - going;
        var schedules = engine.Schedules.For(known.RootPath).Count;
        engine.Workspaces.Remove(known.RootPath);
        users.Audit(account.Name, "workspace forgotten",
            $"{known.RootPath} ({Count(going, "run")} going, {waiting} waiting, {Count(schedules, "schedule")})");
        return $"\u201c{known.Name}\u201d is forgotten. The folder and its run history are where they were; Add folder\u2026 brings it back. "
               + Going(going, waiting) + " " + Stopped(schedules);
    }

    private static string Going(int going, int waiting) => (going, waiting) switch
    {
        (0, 0) => "No run is going or waiting there.",
        (1, 0) => "1 run going there finishes where it is.",
        (_, 0) => $"{going} runs going there finish where they are.",
        (0, 1) => "1 run waiting there will not start.",
        (0, _) => $"{waiting} runs waiting there will not start.",
        (1, _) => $"1 run going there finishes where it is, and {waiting} waiting will not start.",
        _ => $"{going} runs going there finish where they are, and {waiting} waiting will not start."
    };

    private static string Stopped(int schedules) => schedules switch
    {
        0 => "It has no schedules.",
        1 => "Its 1 schedule stops until the folder is added again.",
        _ => $"Its {schedules} schedules stop until the folder is added again."
    };

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    // As the registry compares folders: on Windows C:\Repo and c:\repo are one.
    private static bool SameFolder(string a, string b)
        => string.Equals(WorkspaceRegistry.Normalise(a), WorkspaceRegistry.Normalise(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ── adding a workspace (Operators) ──────────────────────────────────────

    /// <summary>The roots, or the folders below one of them, for an Operator choosing a workspace; nothing for anybody else.</summary>
    public (IReadOnlyList<string> Folders, string? Problem) Browse(ClaimsPrincipal user, string? folder)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator })
            return ([], "Your role does not include adding workspaces.");
        if (string.IsNullOrWhiteSpace(folder))
            return (workspaces.Roots.All, workspaces.Roots.All.Count == 0 ? workspaces.Roots.Refusal("") : null);
        return workspaces.Roots.Refusal(folder) is { } refused ? ([], refused) : (workspaces.Roots.Below(folder), null);
    }

    /// <summary>Makes a folder under the roots one of the service's workspaces - where its runs, history and inbox are.</summary>
    public string AddWorkspace(ClaimsPrincipal user, string folder)
    {
        if (ServerAuth.StillValid(users, user) is not { Role: >= Role.Operator } account)
            return "Your role does not include adding workspaces.";
        if (workspaces.Roots.Refusal(folder) is { } refused)
            return refused;
        if (!Directory.Exists(folder))
            return $"No such folder: {folder}.";
        var entry = engine.Workspaces.Touch(folder);
        users.Audit(account.Name, "workspace added", entry.RootPath);
        return $"{entry.Name} is a workspace of the service now.";
    }
}

