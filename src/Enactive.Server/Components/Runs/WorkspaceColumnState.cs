namespace Enactive.Server.Components.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.History;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>A workspace in the switcher, named in an address by its id.</summary>
/// <remarks>
/// Public, as a component's parameter type must be; <see cref="Place"/> is internal, as WorkspacePlace is (the window's
/// and the service's one rule, in Enactive.Application).
/// </remarks>
public sealed record SwitcherRow(WorkspaceEntry Entry, Guid Id, bool Exists, bool IsCurrent)
{
    internal WorkspacePlace Place => WorkspaceRows.Place(Exists, IsCurrent);

    public string Detail => WorkspaceRows.Detail(Entry, Exists);
}

/// <summary>A run going now, in any workspace: its title, and what it is doing (RunColumn.Meta) and where.</summary>
internal sealed record RunningRow(Guid Id, string Title, string Meta, string Tooltip);

/// <summary>
/// What the Runs screen's left column shows and does - the window's context column (RunsViewModel and the workspace
/// switcher of MainWindowViewModel) for one person in a browser. A plain class, so what it decides is tested without a
/// page. It offers by role; every action is still decided by the service it calls (ServerHistory, IRunControl), which
/// checks the account again at the action.
/// </summary>
internal sealed class WorkspaceColumnState(ServerHistory history, IRunControl runs, WorkspaceIds ids, ClaimsPrincipal user)
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private RunFilter _filter = RunFilter.All;

    public IReadOnlyList<SwitcherRow> Workspaces { get; private set; } = [];

    /// <summary>The workspace the column is about, or null - none known, or the address named one the service does not know.</summary>
    public WorkspaceEntry? Selected { get; private set; }

    public SwitcherRow? Current => Workspaces.FirstOrDefault(w => w.IsCurrent);

    /// <summary>The selected folder is not on disk: nothing will run there (RunLauncher refuses it).</summary>
    public bool SelectedMissing => Current is { Exists: false };

    /// <summary>
    /// Offered the Operator's actions - Add folder…, Delete these, carry on, forget, delete a run. Only offered: the
    /// service refuses each for anybody else at the action, whatever a page sent.
    /// </summary>
    public bool CanOperate => ServerAuth.RoleOf(user) >= Role.Operator;

    /// <summary>Why the column has nothing to show - an unknown workspace, a history that could not be read - or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>What the last action said, to show under it.</summary>
    public string? Said { get; set; }

    /// <summary>Runs going now, in any workspace, newest first.</summary>
    public IReadOnlyList<RunningRow> Running { get; private set; } = [];

    /// <summary>Runs the process stopped before they ended, with steps left to carry on (the window's UNFINISHED).</summary>
    public IReadOnlyList<RunCheckpoint> Unfinished { get; private set; } = [];

    /// <summary>Every run of the selected workspace.</summary>
    public IReadOnlyList<RunSummary> All { get; private set; } = [];

    /// <summary>
    /// The runs the filter takes (RunHousekeeping.Where, as RunsViewModel.Show) - what "Delete these N" deletes, folded
    /// attempts included: they are on screen as the "×N" of their row.
    /// </summary>
    public IReadOnlyList<RunSummary> Shown { get; private set; } = [];

    /// <summary>The rows drawn: the shown runs with repeat attempts at one task folded into the newest (RunHousekeeping.Rows).</summary>
    public IReadOnlyList<RunHousekeeping.RunRow> Rows { get; private set; } = [];

    public RunFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Show();
        }
    }

    /// <summary>With the count, as the window's (RunRows.DeleteShown).</summary>
    public string DeleteLabel => RunRows.DeleteShown(Shown.Count);

    public bool CanDelete => Shown.Count > 0;

    /// <summary>"3 runs", or "3 of 37 runs" under a filter, as the window's (RunRows.Status).</summary>
    public string Status => RunRows.Status(Rows.Count, All.Count);

    /// <summary>
    /// The workspace an address names by its id, among <paramref name="known"/> (the ones this person may read), or -
    /// with none named - the one used last, as the window opens on the folder it was left in; null when the id names
    /// none of them. The Runs screen's start form takes the same one, so it is chosen in one place.
    /// </summary>
    public static WorkspaceEntry? Choose(IReadOnlyList<WorkspaceEntry> known, Guid? wanted, WorkspaceIds ids)
        => wanted is { } id
            // Found among the workspaces the service knows, and only those: an id is never a way to name a folder.
            ? ids.Find(id) is { } entry && known.Any(k => SamePath(k.RootPath, entry.RootPath)) ? entry : null
            : known.FirstOrDefault();

    /// <summary>The column for the workspace whose id is <paramref name="wanted"/>, or the one used last (<see cref="Choose"/>).</summary>
    public async Task LoadAsync(Guid? wanted, CancellationToken ct)
    {
        var known = history.Workspaces(user);
        Selected = Choose(known, wanted, ids);
        Problem = wanted is not null && Selected is null ? "The service does not know that workspace." : null;

        Workspaces = Switcher(known, Selected, ids);
        await RefreshAsync(ct);
    }

    /// <summary>
    /// The switcher's rows: every workspace in <paramref name="known"/>, the <paramref name="selected"/> one marked - one
    /// rule for every screen that is about one workspace (the Runs screen, Templates), so they mark the same one the same way.
    /// </summary>
    public static IReadOnlyList<SwitcherRow> Switcher(IReadOnlyList<WorkspaceEntry> known, WorkspaceEntry? selected, WorkspaceIds ids)
        => known.Select(k => new SwitcherRow(k, IdOf(ids, k.RootPath), Exists(k.RootPath),
            selected is not null && SamePath(k.RootPath, selected.RootPath))).ToList();

    /// <summary>Reads the selected workspace's history and the runs going now again (the column's Refresh).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        Running = runs.List(user)
            .Where(r => !r.Ended.IsCompleted)
            .Select(Row)
            .ToList();

        if (Selected is null)
        {
            (All, Unfinished) = ([], []);
            Show();
            return;
        }

        var (list, problem) = await history.ListAsync(user, Selected.RootPath, ct);
        Problem = problem ?? (list?.Problem is { } unreadable ? "The history could not be read: " + unreadable : null);
        All = list?.Runs ?? [];
        // A checkpoint with nothing left belongs to a run that finished its steps and died after them: a Resume that runs
        // nothing is a button that lies about what pressing it does (RunsViewModel.ShowResumable).
        Unfinished = list?.Resumable.Where(c => c.IsResumable).ToList() ?? [];
        Show();
    }

    /// <summary>The address of the Runs screen on a workspace.</summary>
    public string Href(SwitcherRow row) => Links.Runs(row.Id);

    // ── attempts ────────────────────────────────────────────────────────────

    public bool IsExpanded(RunSummary lead) => _expanded.Contains(RunHousekeeping.GroupKey(lead));

    /// <summary>Opens or closes one task's attempts - kept by task, so a re-read does not close what somebody opened.</summary>
    public void Toggle(RunSummary lead)
    {
        var key = RunHousekeeping.GroupKey(lead);
        if (!_expanded.Remove(key))
            _expanded.Add(key);
        Show();
    }

    // ── acting (Operators; decided by the services) ─────────────────────────

    /// <summary>The window's question before "Delete these N", with the count; null when there is nothing to delete.</summary>
    public Question? AskDeleteShown() => Shown.Count == 0 ? null : RunHistoryService.DeleteMany(Shown);

    /// <summary>
    /// Deletes the runs that were shown when the question was asked - by id, never by choosing again now: a run that ended
    /// meanwhile would join the filter and go unseen (ServerHistory.DeleteShownAsync).
    /// </summary>
    public async Task DeleteShownAsync(IReadOnlyCollection<Guid> asked, CancellationToken ct)
    {
        if (Selected is null)
            return;
        (Said, _) = await history.DeleteShownAsync(user, Selected.RootPath, asked, ct);
        await RefreshAsync(ct);
    }

    /// <summary>The question before forgetting an unfinished run or deleting one run, in the window's words.</summary>
    public async Task<Question?> AskAsync(string act, Guid id, CancellationToken ct)
    {
        if (Selected is null)
            return null;
        var (question, problem) = await history.AskAsync(user, Selected.RootPath, act, id, null, ct);
        Said = problem;
        return question;
    }

    /// <summary>Does it - <paramref name="choice"/> "confirm" or "alternative" - and reads the column again.</summary>
    public async Task DoAsync(string act, Guid id, string choice, CancellationToken ct)
    {
        if (Selected is null)
            return;
        (Said, _) = await history.DoAsync(user, Selected.RootPath, act, id, null, choice, ct);
        await RefreshAsync(ct);
    }

    /// <summary>Carries an unfinished run on; the run started, or null and why not in <see cref="Said"/>.</summary>
    public async Task<Guid?> ResumeAsync(Guid runId)
    {
        if (Selected is null)
            return null;
        var (run, refusal) = await runs.ResumeAsync(user, Selected.RootPath, runId);
        Said = refusal;
        return run?.Id;
    }

    /// <summary>
    /// Makes a folder under the service's roots a workspace (ServerHistory.AddWorkspace); its id when it is one now, so
    /// the column can switch to it as the window does, else null - and either way what the service said.
    /// </summary>
    public (Guid? Id, string Said) AddWorkspace(string folder)
    {
        var said = history.AddWorkspace(user, folder);
        return (history.Workspace(user, folder) is { } added ? IdOf(added.RootPath) : null, said);
    }

    // ── ─────────────────────────────────────────────────────────────────────

    private void Show()
    {
        Shown = RunHousekeeping.Where(All, _filter, DateTimeOffset.UtcNow);
        Rows = RunHousekeeping.Rows(Shown, _expanded);
    }

    /// <summary>A run in flight as its RUNNING row; internal for the test of the time its tooltip says.</summary>
    internal static RunningRow Row(ServerRun run)
    {
        var title = RunState.Title(run);
        var (done, total) = run.Session.Read((feed, _, _, _) => (feed.StepsDone, feed.StepsTotal));
        // The window's words for a run in flight (RunColumn.Meta); a run still waiting its turn says why instead - the
        // window starts every run at once, the service has a line (RunQueue).
        var meta = run.Waiting is not null
            ? RunState.Of(run)
            : RunColumn.Meta(new Enactive.Core.History.LiveRun(run.Id, run.Id, title, run.Background, done, total, run.StartedAt,
                Waiting: run.Questions.Pending is not null));
        return new RunningRow(run.Id, RunTitle.OneLine(title), meta,
            $"{run.Owner} · {run.Workspace} · started {Times.Clock(run.StartedAt)}");
    }

    // The registry keeps a folder as it was added; one written another way (a trailing separator, another case) is the
    // same workspace (WorkspaceRegistry.Normalise).
    private static bool SamePath(string a, string b)
        => string.Equals(WorkspaceRegistry.Normalise(a), WorkspaceRegistry.Normalise(b), StringComparison.OrdinalIgnoreCase);

    // A known folder whose path cannot be read at all still has its row - red, as missing - with no id to be chosen by:
    // one such entry must not take the column down for every other workspace (as WorkspaceIds.Find steps over it).
    private Guid IdOf(string root) => IdOf(ids, root);

    private static Guid IdOf(WorkspaceIds ids, string root) => ids.TryIdOf(root) ?? Guid.Empty;

    private static bool Exists(string root)
    {
        try { return Directory.Exists(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
