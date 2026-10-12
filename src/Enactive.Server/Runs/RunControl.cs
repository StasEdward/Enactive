namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Workspace;
using Enactive.Core.Events;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Settings;

/// <summary>A run on the service: who started it, where, its place in the line, its session once it runs, its question.</summary>
internal sealed class ServerRun
{
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="reachOf">Where a provider runs, for the run's panel (IRunExecutor.ReachOf); null, Unknown for all.</param>
    public ServerRun(string owner, string workspace, string prompt, RunQueue.Ticket ticket, Guid? taskId = null,
        RunCheckpoint? resume = null, Core.Templates.ResolvedTaskSpec? spec = null, Func<string?, ModelReach>? reachOf = null)
    {
        TaskId = taskId;
        Resume = resume;
        Spec = spec;
        Owner = owner;
        Workspace = workspace;
        Prompt = prompt;
        Ticket = ticket;
        Session = new RunSession(workspace, owner, RunLauncher.Title(prompt), reachOf);
        Questions.Changed += () => Changed?.Invoke();
    }

    public Guid Id => Session.Id;

    /// <summary>The task this run carries on - one stopped at a question, or interrupted - or null for a new task.</summary>
    public Guid? TaskId { get; }

    /// <summary>The step boundary it carries on from, when it has one.</summary>
    public RunCheckpoint? Resume { get; }

    /// <summary>The saved task it is, with its permissions, checks, limits and role - a run tried again from one.</summary>
    public Core.Templates.ResolvedTaskSpec? Spec { get; }

    /// <summary>The schedule that started it, when one did: a run nobody is watching, reported to the inbox.</summary>
    public DueRun? Scheduled { get; init; }

    /// <summary>
    /// Started to run in the background (the window's "Run in background"): nobody is asked its questions - each is parked
    /// for the inbox at once and the run stops at it - and its end, however it comes, is one item in the inbox.
    /// </summary>
    public bool Background { get; init; }
    public string Owner { get; }
    public string Workspace { get; }
    public string Prompt { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public RunQueue.Ticket Ticket { get; }

    /// <summary>
    /// When its turn came and it began to run (UTC), or null while it waits in the line. The ELAPSED tile counts from here,
    /// as the window's stopwatch counts from Run: counted from <see cref="StartedAt"/> it took in another run's time.
    /// </summary>
    public DateTimeOffset? RunningSince { get; internal set; }

    /// <summary>When it was over (UTC), whether it ran or not; null until then. Where the ELAPSED tile stops.</summary>
    public DateTimeOffset? EndedAt { get; private set; }
    public RunSession Session { get; }
    public WebQuestions Questions { get; } = new();
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Why the run is not running yet, while it waits its turn; null once it runs.</summary>
    public string? Waiting => Ended.IsCompleted ? null : Ticket.Waiting;

    /// <summary>Why it never got going - refused by the composer, no engine, cancelled in the line - or null.</summary>
    public string? Refused { get; internal set; }

    /// <summary>Who cancelled it, when somebody did.</summary>
    public string? CancelledBy { get; internal set; }

    /// <summary>Completes when the run is over, whether it ran or not.</summary>
    public Task Ended => _ended.Task;

    /// <summary>It started, asked, was answered, ended. Raised on whatever thread it happened on.</summary>
    public event Action? Changed;

    internal void Notify() => Changed?.Invoke();

    internal void End()
    {
        EndedAt ??= DateTimeOffset.UtcNow;
        _ended.TrySetResult();
        Changed?.Invoke();
    }
}

/// <summary>What the service does with a run once its turn comes: compose it and drain it into its session.</summary>
internal interface IRunExecutor
{
    /// <summary>Why no run can start at all - no engine configured - or null.</summary>
    string? Problem { get; }

    SessionApprovals Session { get; }
    ApprovalStore Approvals { get; }

    /// <summary>
    /// Where a provider runs, by its id - this machine or a cloud - for each run's panel (ModelWorkSplit). Only the
    /// engine's settings know a provider's address; a stand-in with none (a test's, the harness's) leaves every provider
    /// Unknown, which is what the panel shows for a provider it cannot place.
    /// </summary>
    Func<string?, ModelReach> ReachOf => _ => ModelReach.Unknown;

    /// <summary>
    /// Why a background run of <paramref name="owner"/> in <paramref name="root"/> would be refused as it was composed, in
    /// the composer's words, or null - asked before it is started, as the window asks (RunComposer.Refusal): a folder that
    /// stages cannot have a run nobody watches. A stand-in with no workspaces to read refuses nothing.
    /// </summary>
    string? BackgroundRefusal(string root, string owner) => null;

    Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct);
}

/// <summary>A refusal or the run started (or waiting its turn).</summary>
internal sealed record StartResult(ServerRun? Run, string? Refusal);

/// <summary>
/// Everything a person can do to runs on the service (ADR 0002 §3): start, list, open, cancel, answer. The Blazor pages
/// are one client of it, the tests another, a desktop connected to the service (phase 4) would be a third.
///
/// <para><b>Authorization is checked here, at the action</b> (ADR 0002 §5), not only on the page: every method takes
/// the person asking and checks their account is still what it was at sign-in and their role is enough. A page that
/// forgot its attribute still cannot start a run for a Viewer, and an account disabled a second ago cannot act even
/// from a page it already had open.</para>
/// </summary>
internal interface IRunControl
{
    /// <param name="shown">
    /// The level, worker and staging the starting page showed; the start is refused when they are no longer what is saved
    /// for the folder (BarSettings). Null for a start that showed none.
    /// </param>
    StartResult Start(ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null);

    /// <summary>
    /// A run nobody will watch, as the window's "Run in background" starts one (BackgroundRun): in the same line and the
    /// same list as every run, its questions parked for the inbox rather than asked, and one inbox item when it ends.
    /// </summary>
    StartResult StartBackground(ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null);

    /// <summary>
    /// Carries on an interrupted run from its last step boundary (crash recovery): its owner or any Operator, after
    /// being told what runs again (RunRecovery). Never on its own - a person decides.
    /// </summary>
    Task<StartResult> ResumeAsync(ClaimsPrincipal user, string workspace, Guid runId);

    /// <summary>
    /// A past run again - the same run as it was (Retry) or its template as it stands now (Run again) - as an attempt at
    /// the same task (RunHistoryService.Retry/RunAgain).
    /// </summary>
    StartResult Again(ClaimsPrincipal user, string workspace, Application.Rerun again, string action);

    /// <summary>
    /// A schedule's run, from the service's scheduler (ADR 0002 §10): in the same line as everybody's runs, nobody
    /// watching it - its questions are refused, as the console's scheduled runs' are - and its end in the inbox.
    /// </summary>
    StartResult StartScheduled(DueRun due);

    /// <summary>
    /// Why this person's run in this folder would be refused now, or null - asked before something is spent on it, as
    /// an inbox answer is (ServerInbox): the refusal comes from the same checks a start makes.
    /// </summary>
    string? Refusal(ClaimsPrincipal user, string workspace);

    /// <summary>Carries on a run that stopped at a question, now answered in the inbox: under the same task, as a new run.</summary>
    Task<StartResult> ContinueAsync(ClaimsPrincipal user, string workspace, Agents.ParkedDecision parked, string title);
    IReadOnlyList<ServerRun> List(ClaimsPrincipal user);
    ServerRun? Find(ClaimsPrincipal user, Guid id);
    string? Cancel(ClaimsPrincipal user, Guid id);
    string? Answer(ClaimsPrincipal user, Guid id, Guid question, string choice);

    /// <summary>
    /// How long a question asked in the browser waits before the run stops at it and it goes to the inbox; null, as long as
    /// it takes. Said on the question's card ("waits 12 min more"), so an Operator can tell an answer needed now from one
    /// that can wait for the inbox.
    /// </summary>
    TimeSpan? QuestionWait { get; }
}

/// <param name="parkQuestionsAfter">How long a question waits before the run stops at it for the inbox; null, never.</param>
/// <param name="roots">Where runs from the browser may work; null for anywhere (a test's).</param>
/// <param name="settingsChanged">
/// Why the settings a page showed are no longer the folder's saved ones, or null (BarSettings.ChangedFor); null, never
/// asked (a test's).
/// </param>
internal sealed class RunControl(UserStore users, RunQueue queue, IRunExecutor executor, ILogger<RunControl> log,
    TimeSpan? parkQuestionsAfter = null, WorkspaceRoots? roots = null, Func<string, BarSettings, string?>? settingsChanged = null) : IRunControl
{
    private readonly object _gate = new();
    private readonly List<ServerRun> _runs = new();
    private bool _stopping;

    public TimeSpan? QuestionWait => parkQuestionsAfter;

    /// <summary>How many finished runs stay listed here; older ones are read from the history (1.6).</summary>
    private const int KeepFinished = 50;

    public StartResult Start(ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null)
        => StartRun(user, workspace, prompt, null, null, "run started", shown: shown);

    public StartResult StartBackground(ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null)
        => StartRun(user, workspace, prompt, null, null, "run started in the background", background: true, shown: shown);

    public StartResult StartScheduled(DueRun due)
    {
        var root = Workspace.WorkspaceRegistry.Normalise(due.Schedule.WorkspaceRoot);
        // Held to the roots as a person's run is: the schedules file is also the account's desktop's and console tick's,
        // and a schedule there for a folder outside the roots ran an agent there every time it came due.
        if ((RunLauncher.Refusal(root, executor.Problem) ?? roots?.Refusal(root)) is { } refused)
        {
            due.Hold.Dispose();
            return new(null, refused);
        }
        ServerRun run;
        lock (_gate)
        {
            if (_stopping)
            {
                due.Hold.Dispose();
                return new(null, "The service is stopping.");
            }
            run = new ServerRun("schedule: " + due.Schedule.Name, root, due.Spec.Goal, queue.Enqueue(root), spec: due.Spec,
                reachOf: executor.ReachOf)
            {
                Scheduled = due
            };
            _runs.Add(run);
        }
        users.Audit("(scheduler)", "scheduled run started", $"{root}: {due.Schedule.Name} - {due.Why}");
        _ = LifeAsync(run, run.Owner);
        return new(run, null);
    }

    public StartResult Again(ClaimsPrincipal user, string workspace, Application.Rerun again, string action)
        => StartRun(user, workspace, again.Goal, again.TaskId, null, action, again.Spec);

    public async Task<StartResult> ResumeAsync(ClaimsPrincipal user, string workspace, Guid runId)
    {
        if (Allowed(user, Role.Operator) is null)
            return new(null, NotAllowed(user, "resume a run"));
        var root = Workspace.WorkspaceRegistry.Normalise(workspace);
        var checkpoints = await new JsonCheckpointStore(WorkspaceInfo.For(root)).LoadAllAsync(CancellationToken.None);
        if (checkpoints.FirstOrDefault(c => c.RunId == runId) is not { } checkpoint)
            return new(null, "There is no such unfinished run in that folder.");
        if (!checkpoint.IsResumable)
            return new(null, "That run has no step left to run; it can only be forgotten.");
        return StartRun(user, root, checkpoint.Request, checkpoint.TaskId, checkpoint, "run resumed");
    }

    public async Task<StartResult> ContinueAsync(ClaimsPrincipal user, string workspace, Agents.ParkedDecision parked, string title)
    {
        if (Allowed(user, Role.Operator) is null)
            return new(null, NotAllowed(user, "carry a run on"));
        var root = Workspace.WorkspaceRegistry.Normalise(workspace);
        var checkpoint = await BackgroundRun.ContinueFromAsync(parked, root, warning => log.LogWarning("{Warning}", warning), CancellationToken.None);
        return StartRun(user, root, checkpoint?.Request ?? title, parked.TaskId, checkpoint, "run carried on");
    }

    private StartResult StartRun(ClaimsPrincipal user, string workspace, string prompt, Guid? taskId, RunCheckpoint? resume, string action,
        Core.Templates.ResolvedTaskSpec? spec = null, bool background = false, BarSettings? shown = null)
    {
        if (string.IsNullOrWhiteSpace(prompt) && Allowed(user, Role.Operator) is not null)
            return new(null, "Say what the run is to do.");
        if (Check(user, workspace, out var account, out var root) is { } refused)
            return new(null, refused);
        // What the person saw is what the run goes with: a level another Operator saved after the page read it would
        // otherwise run unseen (BarSettings).
        if (shown is not null && settingsChanged?.Invoke(root, shown) is { } changed)
            return new(null, changed);
        // Refused here, where it was asked for, rather than as it is composed - by then it has taken a place in the line,
        // and its only word would be an inbox item saying it failed to start.
        if (background && executor.BackgroundRefusal(root, account!.Name) is { } notInTheBackground)
            return new(null, notInTheBackground);

        ServerRun run;
        lock (_gate)
        {
            if (_stopping)
                return new(null, "The service is stopping; start the run again when it is back.");
            run = new ServerRun(account!.Name, root, prompt.Trim(), queue.Enqueue(root), taskId, resume, spec, executor.ReachOf)
            {
                Background = background
            };
            _runs.Add(run);
            var finished = _runs.Where(r => r.Ended.IsCompleted).ToList();
            foreach (var old in finished.Take(Math.Max(0, finished.Count - KeepFinished)))
                _runs.Remove(old);
        }
        users.Audit(account.Name, action, $"{root}: {run.Prompt}");
        _ = LifeAsync(run, account.Name);
        return new(run, null);
    }

    public string? Refusal(ClaimsPrincipal user, string workspace) => Check(user, workspace, out _, out _);

    /// <summary>Every check a start makes but the task's words: who asks, the folder, the engine, the service's state.</summary>
    private string? Check(ClaimsPrincipal user, string workspace, out Account? account, out string root)
    {
        root = "";
        if ((account = Allowed(user, Role.Operator)) is null)
            return NotAllowed(user, "start a run");
        if (string.IsNullOrWhiteSpace(workspace))
            return "Name the folder the run is to work in.";

        root = Workspace.WorkspaceRegistry.Normalise(workspace);
        // The same checks every host makes before a run (RunLauncher): a folder that is there, an engine to run on - and
        // the service's own: a folder under its roots (WorkspaceRoots).
        if ((RunLauncher.Refusal(root, executor.Problem) ?? roots?.Refusal(root)) is { } refused)
            return refused;
        lock (_gate)
            return _stopping ? "The service is stopping; start the run again when it is back." : null;
    }

    /// <summary>The run from its place in the line to its end. Never throws: what went wrong is the run's to say.</summary>
    private async Task LifeAsync(ServerRun run, string owner)
    {
        var ct = run.Cancellation.Token;
        var running = false;
        try
        {
            // Its turn: a free slot and nobody else in its folder (RunQueue). Said meanwhile by run.Waiting.
            await run.Ticket.Started.WaitAsync(ct);
            running = true;
            run.RunningSince = DateTimeOffset.UtcNow;
            run.Notify();
            // The router asks through this run's questions, and remembers what the owner allows as the owner.
            // A schedule's run has nobody to ask: its questions are refused, as the console's are (UnattendedDecisionHandler),
            // and what was allowed for unwatched runs answers before that. A background run's person will be back: its
            // question is parked for the inbox at once, as the window's background run parks it - asked in the browser, it
            // would hold its slot and its folder for a page nobody has open. Everybody else's are asked in the browser.
            IDecisionHandler decisions = run.Scheduled is not null ? new Agents.UnattendedDecisionHandler()
                : run.Background ? new Agents.ParkingDecisionHandler()
                : new DecisionRouter(run.Questions, executor.Session, executor.Approvals, owner, parkQuestionsAfter);
            await executor.ExecuteAsync(run, decisions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only while it waited its turn is a cancelled run one that never ran; one cancelled while running ran, and
            // is shown as cancelled by whoever did it (found by RunControlTests, which saw "before it started" there).
            if (!running)
                run.Refused ??= "Cancelled before it started.";
        }
        catch (Exception ex)
        {
            // Refused by the composer (a workspace another process runs in, staging asked of a web run), no engine.
            run.Refused = ex.Message;
            log.LogWarning("A run of {Owner} in {Workspace} did not start: {Why}", owner, run.Workspace, ex.Message);
        }
        finally
        {
            // A schedule's run that never got going - refused by the composer, cancelled in the line - says so where its
            // owner looks, as one that could not start at all does (ServerScheduler). Its occurrence is spent, and only a
            // warning in the log said it did not run. One that ran files its own item (EngineExecutor).
            if (run.Scheduled is { } due && run.Refused is { } why)
                await ScheduleDidNotRunAsync(run.Workspace, due, why);
            await LeaveInTheInboxAsync(run, running);
            run.Ticket.Dispose();
            // The schedule's claim and its workspace's lease, held from the tick that took it (DueSchedules).
            run.Scheduled?.Hold.Dispose();
            users.Audit(owner, "run ended", run.Workspace + ": " + (run.Refused ?? run.Session.Failure ?? "finished"));
            run.End();
        }
    }

    /// <summary>
    /// What a run leaves in its workspace's inbox as it ends - all but a schedule's, which files its own (EngineExecutor,
    /// <see cref="ScheduleDidNotRunAsync"/>). Here, where every ending passes, rather than in the executor: a background
    /// run that never got to the executor - cancelled in the line, refused as it was composed - must say so too.
    /// </summary>
    /// <param name="ran">Its turn came: false for one cancelled while it waited in the line.</param>
    private async Task LeaveInTheInboxAsync(ServerRun run, bool ran)
    {
        if (run.Scheduled is not null)
            return;
        var workspace = WorkspaceInfo.For(run.Workspace);
        var title = RunState.Title(run);
        InboxItem item;
        if (run.Background)
        {
            // Exactly one item, however it ended: nobody watched it, so the inbox is the only place its end - even an end
            // before it began - can be read. In the window's words (BackgroundRun, BackgroundTally), so a run sent to the
            // background reads the same from the browser as from the desktop.
            var tally = run.Session.Tally;
            if (!ran && run.Cancellation.IsCancellationRequested)
                item = BackgroundRun.CancelledBeforeItRan(workspace, title);
            // Refused before a single event: it never ran. Only then - something that throws after the run's events
            // (keeping its proposals, closing its tools) set Refused too, and a run that did its work read "Failed to start".
            else if (run.Refused is { } why && run.Session.RunId is null)
                item = BackgroundRun.DidNotStart(workspace, title, why);
            else
            {
                // Cancelled as it was composed: no event was drained to say so.
                if (run.Cancellation.IsCancellationRequested && !tally.Ended)
                    tally.Cancelled();
                // It ran, and what came after its events failed before the engine said how it ended.
                else if (run.Refused is { } after && !tally.Ended)
                    tally.Failed(after);
                item = tally.Item(workspace, title);
            }
        }
        else if (run.Session.Outcome == RunOutcomeKind.NeedsUser)
        {
            // Stopped at a question nobody answered in time: the question is in the ledger, and the inbox is where it is
            // found and answered - as a background run's is (InboxLines), so both read the same.
            var line = InboxLines.For(nameof(RunOutcomeKind.NeedsUser), 0, 0, null);
            item = new InboxItem(Guid.NewGuid(), workspace.Id, line.Kind, title, line.Summary, run.Session.RunId ?? Guid.Empty,
                "unread", DateTimeOffset.UtcNow);
        }
        else
            return;

        try
        {
            await InboxStoreFactory.Create(workspace).AppendAsync(item, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Every store the inbox can be (a file, SQLite, MySQL) fails in its own way, and this runs in the run's last
            // moments: a throw here would keep the run from ending and its slot and folder from being let go.
            log.LogWarning("The inbox of {Workspace} could not be told how a run of {Owner} ended: {Why}", run.Workspace, run.Owner, ex.Message);
        }
    }

    private async Task ScheduleDidNotRunAsync(string root, DueRun due, string why)
    {
        try
        {
            var workspace = WorkspaceInfo.For(root);
            await InboxStoreFactory.Create(workspace).AppendAsync(ScheduledOutcome.CouldNotStart(
                workspace.Id, due.Schedule.Id, due.Schedule.Name, why, DateTimeOffset.UtcNow), CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("The inbox of {Workspace} could not be told the schedule {Schedule} did not run: {Why}", root, due.Schedule.Name, ex.Message);
        }
    }

    public IReadOnlyList<ServerRun> List(ClaimsPrincipal user)
    {
        if (Allowed(user, Role.Viewer) is null)
            return [];
        lock (_gate)
            return _runs.OrderByDescending(r => r.StartedAt).ToList();
    }

    public ServerRun? Find(ClaimsPrincipal user, Guid id)
    {
        if (Allowed(user, Role.Viewer) is null)
            return null;
        lock (_gate)
            return _runs.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>The run's owner or any Operator (ADR 0002 §9).</summary>
    public string? Cancel(ClaimsPrincipal user, Guid id)
    {
        if (Allowed(user, Role.Operator) is not { } account)
            return NotAllowed(user, "cancel a run");
        if (Find(user, id) is not { } run)
            return "There is no such run.";
        if (run.Ended.IsCompleted)
            return "The run has already ended.";

        run.CancelledBy = account.Name;
        users.Audit(account.Name, "run cancelled", $"{run.Workspace}: {run.Prompt}" + (account.Name == run.Owner ? "" : $" (started by {run.Owner})"));
        run.Cancellation.Cancel();
        return null;
    }

    /// <summary>
    /// The run's owner or any Operator; the first answer wins and a later one is told it was too late. Only the owner
    /// may answer with an approval to remember: what is remembered binds the person it is remembered for, and one
    /// Operator must not leave standing approvals in another's name.
    /// </summary>
    public string? Answer(ClaimsPrincipal user, Guid id, Guid question, string choice)
    {
        if (Allowed(user, Role.Operator) is not { } account)
            return NotAllowed(user, "answer a run's question");
        if (Find(user, id) is not { } run)
            return "There is no such run.";
        if (run.Questions.Pending is not { } pending || pending.Request.Id != question)
            return "That question has already been answered, or withdrawn.";
        if (pending.Choices.FirstOrDefault(c => c.Label == choice) is not { } chosen)
            return "That is not one of the answers offered.";
        if (chosen.Remember != RememberAs.Nothing && account.Name != run.Owner)
            return $"Only {run.Owner}, who started the run, can allow this for longer than this once.";

        if (!pending.Answer(chosen))
            return "That question has already been answered, or withdrawn.";
        run.Questions.Answered(pending.Request.Id, account.Name, chosen.Label);
        users.Audit(account.Name, "question answered", $"{pending.Request.Topic}: {chosen.Label}" + (account.Name == run.Owner ? "" : $" (run of {run.Owner})"));
        return null;
    }

    /// <summary>
    /// A planned stop of the service (ADR 0002 §6): no new runs, every run cancelled - the engine stops at its next step
    /// boundary and its checkpoint is on disk, so the run is offered for resuming afterwards - and a bounded wait for
    /// them to end. A run that does not end in time is left to the process's exit: the wait is bounded so that a run
    /// stuck in a tool cannot keep the service from stopping, which Windows would then kill without a word.
    /// </summary>
    /// <returns>The runs that had not ended when the wait was up.</returns>
    public async Task<int> StopAsync(TimeSpan wait)
    {
        List<ServerRun> going;
        lock (_gate)
        {
            _stopping = true;
            going = _runs.Where(r => !r.Ended.IsCompleted).ToList();
        }
        foreach (var run in going)
        {
            run.CancelledBy ??= "the service, stopping";
            run.Cancellation.Cancel();
        }
        try { await Task.WhenAll(going.Select(r => r.Ended)).WaitAsync(wait); }
        catch (TimeoutException) { }
        var left = going.Count(r => !r.Ended.IsCompleted);
        if (going.Count > 0)
            log.LogInformation("Stopping: {Cancelled} run(s) cancelled, {Left} not ended in time.", going.Count, left);
        return left;
    }

    /// <summary>The account behind the request, when it still speaks for it and its role is enough; null otherwise.</summary>
    private Account? Allowed(ClaimsPrincipal user, Role needed)
        => ServerAuth.StillValid(users, user) is { } account && account.Role >= needed ? account : null;

    private string NotAllowed(ClaimsPrincipal user, string what)
        => ServerAuth.StillValid(users, user) is null
            ? "Your session has ended. Sign in again."
            : $"Your role does not include the right to {what}.";
}

/// <summary>The engine's side of a run on the service: composed as a run from the web, drained into its session.</summary>
internal sealed class EngineExecutor(ServerEngine engine, ServerStaging staging) : IRunExecutor
{
    public string? Problem => engine.Problem;
    public SessionApprovals Session => engine.Current?.Session ?? new SessionApprovals();
    public ApprovalStore Approvals => engine.Approvals;

    /// <summary>The settings as they are when asked - a provider's address saved since the run began is the one it has now.</summary>
    public Func<string?, ModelReach> ReachOf
        => id => engine.Settings is { } settings ? ModelReaches.Of(settings, id) : ModelReach.Unknown;

    public string? BackgroundRefusal(string root, string owner)
        => engine.Workspaces.Find(root) is { } saved
            ? RunComposer.Refusal(BackgroundRequest(WorkspaceInfo.For(root), "", owner, saved))
            : null;

    /// <summary>
    /// A folder set to stage keeps a browser run's proposals on disk, for whoever applies them later (ServerStaging). Not
    /// a scheduled or a background run's - nobody watches them (RunComposer refuses that) - nor a resumed one, which
    /// never stages.
    /// </summary>
    internal static bool KeepsProposals(ServerRun run, WorkspaceEntry saved)
        => run.Scheduled is null && !run.Background && saved.StageChanges && run.Resume is null;

    /// <summary>The request a run is composed from: who it is for, whether anybody watches it, and the folder's settings.</summary>
    internal static RunRequest RequestFor(ServerRun run, WorkspaceInfo workspace, WorkspaceEntry saved, string? keepProposalsIn)
    {
        // The service account's own run, like the console's tick: no principal, its unwatched approvals.
        if (run.Scheduled is not null)
            return new RunRequest(workspace, run.Prompt, IntentSource.Schedule, Spec: run.Spec) { Defaults = saved.Defaults };
        if (run.Background)
            return BackgroundRequest(workspace, run.Prompt, run.Owner, saved, run.TaskId, run.Resume, run.Spec);
        // A run from the browser (IntentSource.Web): attended, with the shell when the folder's level allows it, and
        // the person who started it named - whose remembered approvals answer for it (RunComposer, ApprovalStore rule 5).
        return new RunRequest(workspace, run.Prompt, IntentSource.Web, run.TaskId, run.Resume, run.Spec,
            Stage: saved.StageChanges, Principal: run.Owner)
        {
            Defaults = saved.Defaults,
            KeepProposalsIn = keepProposalsIn
        };
    }

    /// <summary>
    /// A background run from the browser, composed as the window's background run is (IntentSource.Inbox): nobody watches
    /// it. It was composed as a browser run (Web), which the composer reads as watched, and the owner's "Allow (workspace)"
    /// answered its questions - a plain approval holds only where somebody is watching (ApprovalStore, rule 4). Its owner is
    /// still named: only their own approvals for unwatched runs answer for it (rule 5). The folder's staging is asked for as
    /// it is, so a folder that stages is refused in the composer's words rather than written to directly.
    /// </summary>
    private static RunRequest BackgroundRequest(WorkspaceInfo workspace, string prompt, string owner, WorkspaceEntry saved,
        Guid? taskId = null, RunCheckpoint? resume = null, Core.Templates.ResolvedTaskSpec? spec = null)
        => new(workspace, prompt, IntentSource.Inbox, taskId, resume, spec, Stage: saved.StageChanges, Principal: owner)
        {
            Defaults = saved.Defaults
        };

    public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
    {
        // First, before anything is composed: the engine is the service's one and reports every model call through one
        // callback, while several runs go at once. The callback finds the run by this flow (ServerRunScope) - set
        // here, it is this run's for all the engine does on its behalf, and gone when this method returns.
        ServerRunScope.Current = run.Session;
        var composed = engine.Current ?? throw new InvalidOperationException(engine.Problem ?? "There is no engine to run on.");
        var workspace = WorkspaceInfo.Adopt(run.Workspace);
        var saved = engine.Workspaces.Find(run.Workspace) ?? engine.Workspaces.Touch(run.Workspace);

        var scheduled = run.Scheduled;
        var keep = KeepsProposals(run, saved) ? staging.Begin(run.Workspace, run.Id, run.Owner, run.Prompt) : null;
        var request = RequestFor(run, workspace, saved, keep);
        Core.Artifacts.IArtifactStore? artifacts = null;
        try
        {
            await using var composedRun = await RunComposer.ComposeAsync(composed, request, decisions, ct);
            artifacts = composedRun.Artifacts;
            await run.Session.DrainAsync(composedRun.Events(ct), ct);
        }
        finally
        {
            if (keep is not null)
                await staging.EndAsync(run.Workspace, keep, RunState.Title(run), run.Session.RunId, artifacts);
        }

        // Exactly one inbox item per scheduled run, whatever its ending - the item the console's tick files.
        if (scheduled is not null)
        {
            var store = RunStoreFactory.Create(workspace);
            var header = (await store.LoadSummariesAsync(CancellationToken.None))
                .Where(r => r.RunId == run.Session.RunId).FirstOrDefault();
            var record = header is null ? null : await store.LoadAsync(header.RunId, CancellationToken.None);
            await InboxStoreFactory.Create(workspace).AppendAsync(ScheduledOutcome.For(workspace.Id, scheduled.Schedule.Id,
                scheduled.Schedule.Name, record, DateTimeOffset.UtcNow), CancellationToken.None);
            return;
        }
        // Every other run's inbox item - a background run's end, a question nobody answered in time - is filed as the run
        // ends (RunControl.LeaveInTheInboxAsync), where an ending that never reached this method passes too.
    }
}

/// <summary>Stops the runs when the service stops - a planned stop, as RunControl.StopAsync says.</summary>
internal sealed class RunsStopWithTheService(IRunControl control) : IHostedService
{
    /// <summary>Under the host's own shutdown timeout, so the runs' wait ends before the host gives up on it.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => control is RunControl runs ? runs.StopAsync(Wait) : Task.CompletedTask;
}
