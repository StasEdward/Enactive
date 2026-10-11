namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Settings;

/// <summary>A run on the service: who started it, where, its place in the line, its session once it runs, its question.</summary>
internal sealed class ServerRun
{
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ServerRun(string owner, string workspace, string prompt, RunQueue.Ticket ticket)
    {
        Owner = owner;
        Workspace = workspace;
        Prompt = prompt;
        Ticket = ticket;
        Session = new RunSession(workspace, owner, RunLauncher.Title(prompt));
        Questions.Changed += () => Changed?.Invoke();
    }

    public Guid Id => Session.Id;
    public string Owner { get; }
    public string Workspace { get; }
    public string Prompt { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public RunQueue.Ticket Ticket { get; }
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
    StartResult Start(ClaimsPrincipal user, string workspace, string prompt);
    IReadOnlyList<ServerRun> List(ClaimsPrincipal user);
    ServerRun? Find(ClaimsPrincipal user, Guid id);
    string? Cancel(ClaimsPrincipal user, Guid id);
    string? Answer(ClaimsPrincipal user, Guid id, Guid question, string choice);
}

internal sealed class RunControl(UserStore users, RunQueue queue, IRunExecutor executor, ILogger<RunControl> log) : IRunControl
{
    private readonly object _gate = new();
    private readonly List<ServerRun> _runs = new();
    private bool _stopping;

    /// <summary>How many finished runs stay listed here; older ones are read from the history (1.6).</summary>
    private const int KeepFinished = 50;

    public StartResult Start(ClaimsPrincipal user, string workspace, string prompt)
    {
        if (Allowed(user, Role.Operator) is not { } account)
            return new(null, NotAllowed(user, "start a run"));
        if (string.IsNullOrWhiteSpace(prompt))
            return new(null, "Say what the run is to do.");
        if (string.IsNullOrWhiteSpace(workspace))
            return new(null, "Name the folder the run is to work in.");

        var root = Workspace.WorkspaceRegistry.Normalise(workspace);
        // The same checks every host makes before a run (RunLauncher): a folder that is there, an engine to run on.
        if (RunLauncher.Refusal(root, executor.Problem) is { } refused)
            return new(null, refused);

        ServerRun run;
        lock (_gate)
        {
            if (_stopping)
                return new(null, "The service is stopping; start the run again when it is back.");
            run = new ServerRun(account.Name, root, prompt.Trim(), queue.Enqueue(root));
            _runs.Add(run);
            var finished = _runs.Where(r => r.Ended.IsCompleted).ToList();
            foreach (var old in finished.Take(Math.Max(0, finished.Count - KeepFinished)))
                _runs.Remove(old);
        }
        users.Audit(account.Name, "run started", $"{root}: {run.Prompt}");
        _ = LifeAsync(run, account.Name);
        return new(run, null);
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
            run.Notify();
            // The router asks through this run's questions, and remembers what the owner allows as the owner.
            var decisions = new DecisionRouter(run.Questions, executor.Session, executor.Approvals, owner);
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
            run.Ticket.Dispose();
            users.Audit(owner, "run ended", run.Workspace + ": " + (run.Refused ?? run.Session.Failure ?? "finished"));
            run.End();
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
internal sealed class EngineExecutor(ServerEngine engine) : IRunExecutor
{
    public string? Problem => engine.Problem;
    public SessionApprovals Session => engine.Current?.Session ?? new SessionApprovals();
    public ApprovalStore Approvals => engine.Approvals;

    public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
    {
        var composed = engine.Current ?? throw new InvalidOperationException(engine.Problem ?? "There is no engine to run on.");
        var workspace = WorkspaceInfo.Adopt(run.Workspace);
        var saved = engine.Workspaces.Find(run.Workspace) ?? engine.Workspaces.Touch(run.Workspace);

        // A run from the browser (IntentSource.Web): attended, with the shell when the folder's level allows it, and
        // the person who started it named - whose remembered approvals answer for it (RunComposer, ApprovalStore rule 5).
        var request = new RunRequest(workspace, run.Prompt, IntentSource.Web, Stage: saved.StageChanges, Principal: run.Owner)
        {
            Defaults = saved.Defaults
        };
        await using var composedRun = await RunComposer.ComposeAsync(composed, request, decisions, ct);
        await run.Session.DrainAsync(composedRun.Events(ct), ct);
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
