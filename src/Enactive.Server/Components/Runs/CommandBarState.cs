namespace Enactive.Server.Components.Runs;

using System.Security.Claims;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>
/// What the Runs screen's command bar shows and does - the window's command bar (MainWindowViewModel: the verbs, the
/// input, Run / Stop / Run in background, the level, the worker and staging) for one person in a browser. A plain class,
/// so what it decides is tested without a page. It offers by role; every action is still decided by the service it
/// calls (IRunControl, ServerHistory), which checks the account again at the action - what that said is <see cref="Said"/>.
/// </summary>
internal sealed class CommandBarState(IRunControl runs, ServerHistory history, ClaimsPrincipal user)
{
    private string _root = "";

    /// <summary>What is typed in the box.</summary>
    public string Input { get; set; } = "";

    /// <summary>The workspace's level, 0 Observe … <see cref="HighestLevel"/>, as saved for it.</summary>
    public int AutonomyLevel { get; private set; } = 2;

    /// <summary>The worker picked, by id; null when the settings name none to pick from.</summary>
    public string? WorkerId { get; private set; }

    public bool StageChanges { get; private set; }

    /// <summary>The workers the box lists, by id and the name the person knows them by (ServerHistory.Workers).</summary>
    public IReadOnlyList<(string Id, string Name)> Workers { get; private set; } = [];

    /// <summary>The level in words, as the window's label and a run's record say it.</summary>
    public string AutonomyLabel => AutonomyTiers.Describe(AutonomyLevel);

    /// <summary>The label's colour, the tier's (Looks.Autonomy).</summary>
    public string AutonomyTone => Looks.Autonomy(AutonomyLevel);

    /// <summary>The slider's top position: as many as there are tiers, not a literal 3.</summary>
    public static int HighestLevel => AutonomyTiers.Highest;

    /// <summary>
    /// Offered Run, Run in background, Stop and changing the settings. Only offered: the service refuses each for anybody
    /// else at the action, whatever a page sent.
    /// </summary>
    public bool CanOperate => ServerAuth.RoleOf(user) >= Role.Operator;

    /// <summary>A workspace to run in and save to; with none, nothing can be saved.</summary>
    public bool HasWorkspace => _root.Length > 0;

    /// <summary>The run open on the screen, which Stop cancels; null when none is.</summary>
    public Guid? OpenRun { get; set; }

    /// <summary>
    /// The open run is going: as the window's IsBusy, Stop is offered and nothing else starts or changes the settings
    /// meanwhile - one run in the foreground at a time.
    /// </summary>
    public bool IsBusy => OpenRun is { } id && runs.Find(user, id) is { Ended.IsCompleted: false };

    /// <summary>What the last action was refused with, to show under the bar; null after one that went through.</summary>
    public string? Said { get; private set; }

    /// <summary>
    /// The bar for a workspace: its saved level, worker and staging, read again from the service - the column may have
    /// moved to it from a page that saved something else meanwhile.
    /// </summary>
    public void Show(WorkspaceEntry? workspace)
    {
        _root = workspace?.RootPath ?? "";
        Workers = history.Workers();
        var saved = workspace is null ? null : history.Workspace(user, workspace.RootPath) ?? workspace;
        AutonomyLevel = AutonomyTiers.Clamp(saved?.Autonomy ?? 2);
        StageChanges = saved?.StageChanges ?? false;
        // By the rule a run reads it by: the window saved a worker's role name until 2026-10-08, and read by id alone such
        // a folder's worker was not found and the box fell back to the first - which the next save then wrote, changing the
        // workspace's worker without anybody choosing to. None saved, or one gone, is the first, as the window's box is.
        WorkerId = WorkerNames.Resolve(Workers.Select(w => (w.Id, w.Name)), saved?.WorkerId) ?? Workers.FirstOrDefault().Id;
        Said = null;
    }

    /// <summary>A verb chip: the verb in front of what is typed.</summary>
    public void Prefix(string verb) => Input = CommandVerbs.Prefix(Input, verb);

    /// <summary>Starts what is typed in the workspace, to be watched: the run's id, or null and why not in <see cref="Said"/>.</summary>
    public Guid? Run()
    {
        var (run, refusal) = runs.Start(user, _root, Input);
        return Started(run, refusal);
    }

    /// <summary>
    /// Starts what is typed in the background (the window's "Run in background"): the bar stays, cleared for the next one.
    /// A workspace that stages is refused at once in the composer's words - a run nobody watches cannot stage.
    /// </summary>
    public bool RunInBackground()
    {
        var (run, refusal) = runs.StartBackground(user, _root, Input);
        return Started(run, refusal) is not null;
    }

    private Guid? Started(ServerRun? run, string? refusal)
    {
        Said = run is null ? refusal : null;
        if (run is null)
            return null;
        // Cleared once it is on its way, as the window's box is; a refused one keeps what was typed, to be run elsewhere.
        Input = "";
        return run.Id;
    }

    /// <summary>Cancels the open run (the window's Stop).</summary>
    public void Stop()
    {
        if (OpenRun is { } id)
            Said = runs.Cancel(user, id);
    }

    /// <summary>The slider moved; true when the level is saved.</summary>
    public bool SetLevel(int level) => Save(level, WorkerId, StageChanges);

    /// <summary>Another worker picked; true when it is saved.</summary>
    public bool SetWorker(string? workerId) => Save(AutonomyLevel, string.IsNullOrEmpty(workerId) ? null : workerId, StageChanges);

    /// <summary>Stage changes ticked or not; true when it is saved.</summary>
    public bool SetStageChanges(bool stage) => Save(AutonomyLevel, WorkerId, stage);

    /// <summary>
    /// Saves the workspace's settings as the window does on every change; true when saved. Refused, the bar keeps what is
    /// saved: left at what was asked, it would say a level or a worker the next run will not have.
    /// </summary>
    private bool Save(int level, string? workerId, bool stage)
    {
        Said = history.SaveSettings(user, _root, level, workerId, stage);
        if (Said is not null)
            return false;
        (AutonomyLevel, WorkerId, StageChanges) = (level, workerId, stage);
        return true;
    }
}
