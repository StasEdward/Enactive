namespace Enactive.Server.Tests;

using System.Security.Claims;
using System.Threading.Channels;
using Enactive.Application;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Everything a person does to runs on the service, through the one interface every page uses (IRunControl, server
/// plan 1.5) - with the engine replaced by a script, so what is under test is the service's part: who may do what,
/// the line of runs, the question a run waits on, and that the run is not the page's.
/// </summary>
public sealed class RunControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-control", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly UserStore _users;
    private readonly Script _engine;
    private readonly RunControl _control;

    private const string Password = "correct horse battery";

    public RunControlTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        _users = new UserStore(Path.Combine(_root, "data"));
        _users.Add("alice", Password, Role.Operator);
        _users.Add("bob", Password, Role.Operator);
        _users.Add("vera", Password, Role.Viewer);
        _engine = new Script(new ApprovalStore(Path.Combine(_root, "permissions.json")));
        _control = new RunControl(_users, new RunQueue(maxActive: 2), _engine, NullLogger<RunControl>.Instance);
    }

    public void Dispose()
    {
        _engine.ReleaseAll();
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    /// <summary>
    /// The engine, scripted: each run starts a step, asks one bound question when told to, and ends when released.
    /// </summary>
    private sealed class Script(ApprovalStore approvals) : IRunExecutor
    {
        private readonly List<TaskCompletionSource> _releases = new();
        public bool Ask { get; set; }

        /// <summary>Refused as the composer refuses a run - the workspace held by another process - before any event.</summary>
        public string? Refuse { get; set; }
        public List<string> Answers { get; } = new();

        /// <summary>Who each run was given to answer its questions, by type - as the run was composed with it.</summary>
        public List<Type> Handlers { get; } = new();

        /// <summary>How the engine says the run ended once released; null, Completed with no payload as before.</summary>
        public RunOutcomeKind? EndWith { get; set; }

        /// <summary>
        /// A parked question ends the run at it, NeedsUser, as the engine's orchestrator ends it - rather than escaping the
        /// executor, which is what the other tests' timed-out questions do.
        /// </summary>
        public bool StopAtAParkedQuestion { get; set; }

        /// <summary>Thrown after the run's events have all been drained - as keeping its proposals or closing its tools can.</summary>
        public string? ThrowAfterDrain { get; set; }

        /// <summary>What the executor says of a background run before it starts (IRunExecutor.BackgroundRefusal).</summary>
        public string? RefuseBackground { get; set; }
        public string? BackgroundRefusal(string root, string owner) => RefuseBackground;
        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
        {
            lock (Handlers) Handlers.Add(decisions.GetType());
            if (Refuse is { } refusal)
                throw new InvalidOperationException(refusal);
            var events = Channel.CreateUnbounded<WorkEvent>();
            var drained = run.Session.DrainAsync(events.Reader.ReadAllAsync(ct), ct);
            var runId = Guid.NewGuid();
            WorkEvent Ev(EventKind kind, string summary) => new(Guid.NewGuid(), Guid.NewGuid(), runId, DateTimeOffset.UtcNow, kind, summary, null);

            events.Writer.TryWrite(Ev(EventKind.StepStarted, "step 1: write the digest"));
            if (Ask)
            {
                try
                {
                    var outcome = await decisions.RequestAsync(new DecisionRequest(Guid.NewGuid(), "Approve write_file", "digest.md",
                        [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "write_file",
                        Action: new(runId, Guid.NewGuid().ToString(), "write_file", "{}", run.Workspace)), ct);
                    lock (Answers) Answers.Add(outcome.OptionId);
                }
                catch (Enactive.Agents.DecisionPendingException parked) when (StopAtAParkedQuestion)
                {
                    events.Writer.TryWrite(new(Guid.NewGuid(), Guid.NewGuid(), runId, DateTimeOffset.UtcNow, EventKind.TaskFailed,
                        nameof(RunOutcomeKind.NeedsUser), WorkEventPayload.OutcomePayload(RunOutcomeKind.NeedsUser, parked.Request.Topic)));
                    events.Writer.Complete();
                    await drained;
                    return;
                }
            }
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_releases) _releases.Add(release);
            await release.Task.WaitAsync(ct);
            events.Writer.TryWrite(EndWith is { } ending
                ? new(Guid.NewGuid(), Guid.NewGuid(), runId, DateTimeOffset.UtcNow, EventKind.TaskFailed, ending.ToString(),
                    WorkEventPayload.OutcomePayload(ending, "Approve write_file"))
                : Ev(EventKind.TaskCompleted, "Completed"));
            events.Writer.Complete();
            await drained;
            if (ThrowAfterDrain is { } after)
                throw new IOException(after);
        }

        public void ReleaseAll()
        {
            lock (_releases) foreach (var r in _releases) r.TrySetResult();
        }

        public async Task ReleaseNextAsync()
        {
            await WaitUntil(() => { lock (_releases) return _releases.Any(r => !r.Task.IsCompleted); });
            lock (_releases) _releases.First(r => !r.Task.IsCompleted).TrySetResult();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    // ── who may ─────────────────────────────────────────────────────────────

    /// <summary>Checked at the action, not only on the page: a Viewer's request to start is refused here.</summary>
    [Fact]
    public void A_viewer_cannot_start_a_run_whatever_page_asked()
    {
        var (run, refusal) = _control.Start(As("vera"), _workspace, "write the digest");

        Assert.Null(run);
        Assert.Contains("role does not include", refusal);
    }

    /// <summary>Disabled a second ago: the page it still has open acts for nobody.</summary>
    [Fact]
    public void An_account_disabled_after_sign_in_cannot_act()
    {
        var alice = As("alice");
        _users.SetDisabled("alice", true);

        Assert.Contains("Sign in again", _control.Start(alice, _workspace, "write the digest").Refusal);
        Assert.Empty(_control.List(alice));
    }

    [Fact]
    public void A_run_in_a_folder_that_is_not_there_is_refused()
        => Assert.Contains("No such folder", _control.Start(As("alice"), Path.Combine(_root, "typo"), "x").Refusal);

    // ── the review's scenario ───────────────────────────────────────────────

    /// <summary>
    /// An Operator starts a task and closes the browser; the run goes on, its question waits; they come back - any page,
    /// any time - find the question and answer it, and the run finishes. Every step of it is in the audit.
    /// </summary>
    [Fact]
    public async Task A_run_outlives_its_page_and_its_question_waits_to_be_answered()
    {
        _engine.Ask = true;
        var started = _control.Start(As("alice"), _workspace, "write the digest").Run!;

        // ...the browser is closed. Later, a new page:
        await WaitUntil(() => started.Questions.Pending is not null);
        var back = _control.Find(As("alice"), started.Id)!;
        var question = back.Questions.Pending!;
        Assert.Equal("Approve write_file", question.Request.Topic);
        Assert.Equal("Waiting for an answer", RunState.Of(back));

        Assert.Null(_control.Answer(As("alice"), back.Id, question.Request.Id, "Allow"));
        await _engine.ReleaseNextAsync();
        await back.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["allow"], _engine.Answers);
        Assert.Null(back.Refused);
        var trail = _users.AuditTrail().Select(e => (e.Account, e.Action)).ToList();
        Assert.Contains(("alice", "run started"), trail);
        Assert.Contains(("alice", "question answered"), trail);
        Assert.Contains(("alice", "run ended"), trail);
    }

    /// <summary>Two agents in one folder edit the same files: the second run waits, says why, and starts after.</summary>
    [Fact]
    public async Task A_second_run_in_the_same_folder_waits_and_says_why_then_runs()
    {
        var first = _control.Start(As("alice"), _workspace, "first").Run!;
        var second = _control.Start(As("bob"), _workspace, "second").Run!;

        Assert.Contains("Another run in this folder", RunState.Of(second));

        await _engine.ReleaseNextAsync();
        await first.Ended.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => second.Waiting is null);
        await _engine.ReleaseNextAsync();
        await second.Ended.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(second.Refused);
    }

    // ── answering ───────────────────────────────────────────────────────────

    /// <summary>Any Operator may answer; the first answer wins and the second is told so.</summary>
    [Fact]
    public async Task The_first_answer_wins()
    {
        _engine.Ask = true;
        var run = _control.Start(As("alice"), _workspace, "write the digest").Run!;
        await WaitUntil(() => run.Questions.Pending is not null);
        var question = run.Questions.Pending!.Request.Id;

        Assert.Null(_control.Answer(As("bob"), run.Id, question, "Deny"));
        Assert.Contains("already been answered", _control.Answer(As("alice"), run.Id, question, "Allow"));

        await WaitUntil(() => _engine.Answers.Count == 1);
        Assert.Equal(["deny"], _engine.Answers);
        Assert.Contains(_users.AuditTrail(), e => e.Account == "bob" && e.Detail!.Contains("(run of alice)"));
    }

    /// <summary>A remembered approval binds the person it is remembered for; one Operator does not leave one in another's name.</summary>
    [Fact]
    public async Task Only_the_owner_can_allow_for_longer_than_once()
    {
        _engine.Ask = true;
        var run = _control.Start(As("alice"), _workspace, "write the digest").Run!;
        await WaitUntil(() => run.Questions.Pending is not null);
        var question = run.Questions.Pending!.Request.Id;

        Assert.Contains("Only alice", _control.Answer(As("bob"), run.Id, question, "Allow (workspace)"));
        Assert.Null(_control.Answer(As("alice"), run.Id, question, "Allow (workspace)"));

        await WaitUntil(() => _engine.Answers.Count == 1);
        Assert.True(_engine.Approvals.Approves(_workspace, "write_file", principal: "alice"));
        Assert.False(_engine.Approvals.Approves(_workspace, "write_file"), "Remembered as the service account's own.");
    }

    [Fact]
    public async Task A_viewer_cannot_answer()
    {
        _engine.Ask = true;
        var run = _control.Start(As("alice"), _workspace, "write the digest").Run!;
        await WaitUntil(() => run.Questions.Pending is not null);

        Assert.Contains("role does not include", _control.Answer(As("vera"), run.Id, run.Questions.Pending!.Request.Id, "Allow"));
    }

    // ── cancelling ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_operator_can_cancel_a_run_and_the_audit_says_whose_it_was()
    {
        var run = _control.Start(As("alice"), _workspace, "write the digest").Run!;
        await WaitUntil(() => run.Session.Seq > 0);

        Assert.Null(_control.Cancel(As("bob"), run.Id));
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Cancelled by bob", RunState.Of(run));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "bob", Action: "run cancelled" } && e.Detail!.Contains("started by alice"));
    }

    [Fact]
    public async Task A_run_cancelled_while_waiting_its_turn_never_starts_and_frees_its_place()
    {
        var first = _control.Start(As("alice"), _workspace, "first").Run!;
        var second = _control.Start(As("alice"), _workspace, "second").Run!;

        _control.Cancel(As("alice"), second.Id);
        await second.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Not run: Cancelled before it started.", RunState.Of(second));
        await _engine.ReleaseNextAsync();
        await first.Ended.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ── a planned stop ──────────────────────────────────────────────────────

    /// <summary>
    /// Stopping the service cancels what runs (the engine stops at its step boundary, its checkpoint on disk) and waits
    /// for it, and starts nothing new - a run started a moment before the stop would otherwise die with the process.
    /// </summary>
    [Fact]
    public async Task A_planned_stop_cancels_the_runs_waits_for_them_and_starts_none()
    {
        var going = _control.Start(As("alice"), _workspace, "write the digest").Run!;
        await WaitUntil(() => going.Session.Seq > 0);

        var left = await _control.StopAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, left);
        Assert.True(going.Ended.IsCompleted);
        Assert.Equal("Cancelled by the service, stopping", RunState.Of(going));
        Assert.Contains("stopping", _control.Start(As("alice"), _workspace, "too late").Refusal);
    }

    /// <summary>A run that will not end - stuck in a tool - does not keep the service from stopping; it is counted.</summary>
    [Fact]
    public async Task A_run_that_does_not_end_in_time_does_not_hold_the_stop()
    {
        var stuck = new RunControl(_users, new RunQueue(2), new Stuck(), NullLogger<RunControl>.Instance);
        var run = stuck.Start(As("alice"), _workspace, "stuck").Run!;
        await WaitUntil(() => run.Waiting is null);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var left = await stuck.StopAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, left);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A run is started from a page, on its circuit's synchronization context; the engine goes on the thread pool, never on
    /// that context. Run there, the engine's synchronous work froze the page that pressed Run, every run from one tab went
    /// one at a time through it, and a blocking wait in the engine could deadlock it.
    /// </summary>
    [Fact]
    public async Task A_run_started_from_a_page_runs_off_the_page_s_thread()
    {
        var executor = new WhereItRuns();
        var control = new RunControl(_users, new RunQueue(2), executor, NullLogger<RunControl>.Instance);
        var page = new PageContext();
        var before = SynchronizationContext.Current;
        ServerRun foreground, background;
        SynchronizationContext.SetSynchronizationContext(page);
        try
        {
            foreground = control.Start(As("alice"), _workspace, "write the digest").Run!;
            background = control.StartBackground(As("alice"), _workspace, "digest the inbox").Run!;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }

        await Task.WhenAll(foreground.Ended, background.Ended).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, executor.Contexts.Count);
        Assert.All(executor.Contexts, context => Assert.Null(context));
    }

    /// <summary>A page's context: what is posted to it runs with it as the current context, as a circuit's does.</summary>
    private sealed class PageContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
            => ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                d(state);
            });
    }

    /// <summary>Records the synchronization context the engine was started on, and ends the run.</summary>
    private sealed class WhereItRuns : IRunExecutor
    {
        public System.Collections.Concurrent.ConcurrentQueue<SynchronizationContext?> Contexts { get; } = new();
        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));

        public Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
        {
            Contexts.Enqueue(SynchronizationContext.Current);
            return Task.CompletedTask;
        }
    }

    private sealed class Stuck : IRunExecutor
    {
        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        public Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct) => Task.Delay(Timeout.Infinite);
    }

    // ── phase 2: a question kept for the inbox, an interrupted run resumed ─

    /// <summary>A question nobody answers in time stops the run at it, rather than holding its slot and folder.</summary>
    [Fact]
    public async Task A_question_nobody_answers_in_time_stops_the_run_at_it()
    {
        _engine.Ask = true;
        var control = new RunControl(_users, new RunQueue(2), _engine, NullLogger<RunControl>.Instance, TimeSpan.FromMilliseconds(150));

        var run = control.Start(As("alice"), _workspace, "write the digest").Run!;
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(run.Questions.Pending);
        Assert.Contains("Waiting for a decision", run.Refused);
    }

    /// <summary>
    /// The card says how long the question still waits before it goes to the inbox: so the question says when it was asked,
    /// and the service how long a question waits. Without them the page could only say that it waits, not for how long -
    /// and an Operator could not tell an answer needed now from one that can wait for the inbox.
    /// </summary>
    [Fact]
    public async Task A_question_says_when_it_was_asked_and_the_service_how_long_it_waits()
    {
        _engine.Ask = true;
        var control = new RunControl(_users, new RunQueue(2), _engine, NullLogger<RunControl>.Instance, TimeSpan.FromMinutes(30));
        var before = DateTimeOffset.UtcNow;

        var run = control.Start(As("alice"), _workspace, "write the digest").Run!;
        await WaitUntil(() => run.Questions.Pending is not null);

        Assert.Equal(TimeSpan.FromMinutes(30), control.QuestionWait);
        Assert.InRange(run.Questions.Pending!.AskedAt, before, DateTimeOffset.UtcNow);
        Assert.Null(_control.QuestionWait);
        Assert.Null(control.Cancel(As("alice"), run.Id));
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The ELAPSED tile counts from the run's turn to its end, as the window's stopwatch does from Run to the end - not
    /// from its place in the line, which would count another run's time as this one's.
    /// </summary>
    [Fact]
    public async Task A_run_says_when_its_turn_came_and_when_it_ended()
    {
        var run = _control.Start(As("alice"), _workspace, "write the digest").Run!;
        await WaitUntil(() => run.RunningSince is not null);
        Assert.Null(run.EndedAt);

        await _engine.ReleaseNextAsync();
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(run.EndedAt >= run.RunningSince);
        Assert.True(run.RunningSince >= run.StartedAt);
    }

    private (Enactive.Agents.ParkedDecision Parked, Guid ItemId) ParkedInTheInbox()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var question = new DecisionRequest(taskId, "Approve send_email", "to the team",
            [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "send_email");
        var parked = new Enactive.Agents.DecisionLedger(_workspace).Park(question);

        var workspace = Enactive.Core.Context.WorkspaceInfo.For(_workspace);
        var at = DateTimeOffset.Now;
        Enactive.Workspace.RunStoreFactory.Create(workspace).SaveAsync(new Enactive.Core.History.RunRecord(runId, taskId,
            "mail the digest", "qwen", at, at, "NeedsUser", [], [], []), CancellationToken.None).GetAwaiter().GetResult();
        var itemId = Guid.NewGuid();
        Enactive.Workspace.InboxStoreFactory.Create(workspace).AppendAsync(new Enactive.Core.Inbox.InboxItem(itemId, workspace.Id,
            "decision", "mail the digest", "Waiting for your decision", runId, "unread", at), CancellationToken.None).GetAwaiter().GetResult();
        return (parked, itemId);
    }

    private ServerInbox InboxOf(IRunControl control)
    {
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        engine.Workspaces.Touch(_workspace);
        return new ServerInbox(_users, new KnownWorkspaces(engine, new WorkspaceRoots([_root])), control);
    }

    /// <summary>
    /// The inbox shows the question a run stopped at; answering it carries the run on under the same task - the
    /// recorded answer is what the step is given when it asks again.
    /// </summary>
    [Fact]
    public async Task A_question_answered_in_the_inbox_carries_its_run_on_under_the_same_task()
    {
        var (parked, itemId) = ParkedInTheInbox();
        var inbox = InboxOf(_control);

        var entry = Assert.Single(await inbox.ListAsync(As("vera"), CancellationToken.None));
        Assert.Equal("Approve send_email", entry.Waiting?.Topic);

        var (said, carried) = await inbox.AnswerAsync(As("bob"), _workspace, itemId, "allow", CancellationToken.None);

        Assert.Contains("carries on", said);
        var run = _control.Find(As("bob"), carried!.Value)!;
        Assert.Equal(parked.TaskId, run.TaskId);
        Assert.Equal("bob", run.Owner);
        Assert.Null(Assert.Single(await inbox.ListAsync(As("bob"), CancellationToken.None)).Waiting);
        await _engine.ReleaseNextAsync();
    }

    /// <summary>
    /// Two people answer one question at once: the other's answer is recorded between this inbox reading the question as
    /// waiting and recording its own. This one is told so - and is neither audited as having answered nor marks the item
    /// read: it decided nothing.
    /// </summary>
    [Fact]
    public async Task An_inbox_answer_that_lost_to_another_is_neither_audited_nor_marks_the_item_read()
    {
        var (parked, itemId) = ParkedInTheInbox();
        var inbox = InboxOf(new AnsweredMeanwhile(_control,
            () => new Enactive.Agents.DecisionLedger(_workspace).Answer(parked.TaskId, parked.RequestId, "deny")));

        var (said, carried) = await inbox.AnswerAsync(As("bob"), _workspace, itemId, "allow", CancellationToken.None);

        Assert.Contains("no longer waiting", said);
        Assert.Null(carried);
        Assert.DoesNotContain(_users.AuditTrail(), e => e.Action == "question answered in the inbox");
        var items = await Enactive.Workspace.InboxStoreFactory.Create(Enactive.Core.Context.WorkspaceInfo.For(_workspace))
            .LoadAllAsync(CancellationToken.None);
        Assert.Equal("unread", Assert.Single(items, i => i.Id == itemId).Status);
    }

    /// <summary>
    /// A page holds the run and its question, and nothing on them answers it: only the runs' one door does (Answer), which
    /// checks who answers and with what, and audits it. The router's answer was a delegate on the question a page read.
    /// </summary>
    [Fact]
    public void Nothing_a_page_holds_can_answer_a_question()
    {
        var answer = typeof(Func<DecisionChoice, bool>);
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        foreach (var type in new[] { typeof(ServerRun), typeof(WebQuestions), typeof(WebQuestion) })
        {
            Assert.DoesNotContain(type.GetFields(all), f => f.FieldType == answer);
            Assert.DoesNotContain(type.GetProperties(all), p => p.PropertyType == answer);
            Assert.DoesNotContain(type.GetMethods(all), m => m.GetParameters().Any(p => p.ParameterType == answer));
        }
    }

    /// <summary>The service's runs, with another person's answer recorded as the inbox asks whether a run could start.</summary>
    private sealed class AnsweredMeanwhile(IRunControl inner, Func<bool> answerMeanwhile) : IRunControl
    {
        public string? Refusal(ClaimsPrincipal user, string workspace)
        {
            Assert.True(answerMeanwhile());
            return inner.Refusal(user, workspace);
        }

        public TimeSpan? QuestionWait => inner.QuestionWait;
        public StartResult Start(ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null) => inner.Start(user, workspace, prompt, shown);
        public StartResult StartBackground(ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null) => inner.StartBackground(user, workspace, prompt, shown);
        public Task<StartResult> ResumeAsync(ClaimsPrincipal user, string workspace, Guid runId) => inner.ResumeAsync(user, workspace, runId);
        public StartResult Again(ClaimsPrincipal user, string workspace, Enactive.Application.Rerun again, string action) => inner.Again(user, workspace, again, action);
        public StartResult StartScheduled(Enactive.Application.DueRun due) => inner.StartScheduled(due);
        public Task<StartResult> ContinueAsync(ClaimsPrincipal user, string workspace, Enactive.Agents.ParkedDecision parked, string title) => inner.ContinueAsync(user, workspace, parked, title);
        public IReadOnlyList<ServerRun> List(ClaimsPrincipal user) => inner.List(user);
        public ServerRun? Find(ClaimsPrincipal user, Guid id) => inner.Find(user, id);
        public string? Cancel(ClaimsPrincipal user, Guid id) => inner.Cancel(user, id);
        public string? Answer(ClaimsPrincipal user, Guid id, Guid question, string choice) => inner.Answer(user, id, question, choice);
    }

    /// <summary>
    /// An inbox that cannot be written now - a file store on a share gone away, a database that is down - is said in words:
    /// thrown, marking an item read took the whole page down with it.
    /// </summary>
    [Fact]
    public async Task An_inbox_that_cannot_be_written_is_said_not_thrown()
    {
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        engine.Workspaces.Touch(_workspace);
        var inbox = new ServerInbox(_users, new KnownWorkspaces(engine, new WorkspaceRoots([_root])), _control, _ => new CannotWrite());

        var said = await inbox.MarkReadAsync(As("alice"), _workspace, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("The inbox of project could not be written just now, so the item is still unread. Try again in a moment.", said);
    }

    /// <summary>An inbox store that reads as empty and cannot be written.</summary>
    private sealed class CannotWrite : Enactive.Core.Inbox.IInboxStore
    {
        public Task AppendAsync(Enactive.Core.Inbox.InboxItem item, CancellationToken ct) => throw new IOException("The network path was not found.");
        public Task<IReadOnlyList<Enactive.Core.Inbox.InboxItem>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Enactive.Core.Inbox.InboxItem>>([]);
        public Task MarkReadAsync(Guid id, CancellationToken ct) => throw new IOException("The network path was not found.");
        public Task MarkAllReadAsync(CancellationToken ct) => throw new IOException("The network path was not found.");
    }

    [Fact]
    public async Task A_viewer_reads_the_inbox_but_cannot_answer_it()
    {
        var (_, itemId) = ParkedInTheInbox();
        var inbox = InboxOf(_control);

        var (said, carried) = await inbox.AnswerAsync(As("vera"), _workspace, itemId, "allow", CancellationToken.None);

        Assert.Null(carried);
        Assert.Contains("role does not include", said);
        Assert.NotNull(Assert.Single(await inbox.ListAsync(As("vera"), CancellationToken.None)).Waiting);
    }

    /// <summary>An item filed in a workspace's inbox, read or not, at a time of its own.</summary>
    private static async Task<Guid> FiledAsync(string folder, string status = "unread")
    {
        var workspace = Enactive.Core.Context.WorkspaceInfo.For(folder);
        var id = Guid.NewGuid();
        await Enactive.Workspace.InboxStoreFactory.Create(workspace).AppendAsync(new Enactive.Core.Inbox.InboxItem(id, workspace.Id,
            "result", "digest", "done", Guid.NewGuid(), status, DateTimeOffset.UtcNow), CancellationToken.None);
        return id;
    }

    /// <summary>
    /// Mark all read is the service's whole inbox, as the page lists it - every workspace the service knows, not one. Done
    /// per workspace, the badge went on counting the items of every folder but the first.
    /// </summary>
    [Fact]
    public async Task Mark_all_read_marks_every_workspace_s_items()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        engine.Workspaces.Touch(_workspace);
        engine.Workspaces.Touch(other);
        var inbox = new ServerInbox(_users, new KnownWorkspaces(engine, new WorkspaceRoots([_root])), _control);
        await FiledAsync(_workspace);
        await FiledAsync(other);
        await FiledAsync(other);
        var told = 0;
        inbox.Changed += () => told++;

        Assert.Null(await inbox.MarkAllReadAsync(As("alice"), CancellationToken.None));

        var entries = await inbox.ListAsync(As("vera"), CancellationToken.None);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal("read", e.Item.Status));
        // Whoever shows the count (the rail's badge) is told, rather than finding out at the next page.
        Assert.Equal(1, told);
    }

    /// <summary>What is read is the service's, the same for everybody: a Viewer reads the inbox but does not change it.</summary>
    [Fact]
    public async Task A_viewer_marks_nothing_read()
    {
        var item = await FiledAsync(_workspace);
        var inbox = InboxOf(_control);

        Assert.Contains("role does not include", await inbox.MarkAllReadAsync(As("vera"), CancellationToken.None));
        Assert.Contains("role does not include", await inbox.MarkReadAsync(As("vera"), _workspace, item, CancellationToken.None));

        Assert.Equal("unread", Assert.Single(await inbox.ListAsync(As("vera"), CancellationToken.None)).Item.Status);
    }

    [Fact]
    public async Task An_operator_marks_one_item_read_in_its_workspace()
    {
        var item = await FiledAsync(_workspace);
        var left = await FiledAsync(_workspace);
        var inbox = InboxOf(_control);

        Assert.Null(await inbox.MarkReadAsync(As("alice"), _workspace, item, CancellationToken.None));
        Assert.Contains("does not know", await inbox.MarkReadAsync(As("alice"), Path.Combine(_root, "elsewhere"), left, CancellationToken.None));

        var entries = await inbox.ListAsync(As("alice"), CancellationToken.None);
        Assert.Equal("read", entries.Single(e => e.Item.Id == item).Item.Status);
        Assert.Equal("unread", entries.Single(e => e.Item.Id == left).Item.Status);
    }

    /// <summary>The item's run, as the inbox shows it: its header from the list, its timeline read for it alone.</summary>
    [Fact]
    public async Task An_item_carries_its_run_and_its_timeline_is_read_for_it()
    {
        var (_, itemId) = ParkedInTheInbox();
        var inbox = InboxOf(_control);

        var entry = Assert.Single(await inbox.ListAsync(As("vera"), CancellationToken.None));
        Assert.Equal("mail the digest", entry.Run?.Title);
        Assert.NotNull(await inbox.TimelineAsync(As("vera"), _workspace, entry.Item.RunId, CancellationToken.None));
        Assert.Null(await inbox.TimelineAsync(As("vera"), Path.Combine(_root, "elsewhere"), entry.Item.RunId, CancellationToken.None));
        Assert.Equal(itemId, entry.Item.Id);
    }

    private RunCheckpoint Interrupted(params string[] statuses)
    {
        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "mail the digest", "mail the digest", null, null,
            statuses.Select((s, i) => new CheckpointStep(Guid.NewGuid(), $"step {i + 1}", [], "low", s)).ToList(),
            [], [], [], 1, 0);
        new Enactive.Workspace.JsonCheckpointStore(Enactive.Core.Context.WorkspaceInfo.For(_workspace))
            .SaveAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult();
        return checkpoint;
    }

    /// <summary>Crash recovery is a person's click, by the owner or any Operator: the run carries on from its boundary.</summary>
    [Fact]
    public async Task An_interrupted_run_is_resumed_from_its_checkpoint_as_the_same_task()
    {
        var checkpoint = Interrupted("Done", "Running", "Pending");

        var (run, refusal) = await _control.ResumeAsync(As("bob"), _workspace, checkpoint.RunId);

        Assert.Null(refusal);
        Assert.Equal(checkpoint.TaskId, run!.TaskId);
        Assert.Equal(checkpoint.RunId, run.Resume!.RunId);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "bob", Action: "run resumed" });
        await _engine.ReleaseNextAsync();
    }

    [Fact]
    public async Task A_run_with_nothing_left_to_run_is_not_resumed_and_a_viewer_resumes_nothing()
    {
        var finished = Interrupted("Done", "Done");
        var interrupted = Interrupted("Running");

        Assert.Contains("no step left", (await _control.ResumeAsync(As("alice"), _workspace, finished.RunId)).Refusal);
        Assert.Contains("role does not include", (await _control.ResumeAsync(As("vera"), _workspace, interrupted.RunId)).Refusal);
    }

    /// <summary>Said before the button: the step that was running runs again, and what it did outside may happen twice.</summary>
    [Fact]
    public void Resuming_says_which_step_runs_again_and_what_may_be_done_twice()
    {
        var warning = Enactive.Application.RunRecovery.Warning(Interrupted("Done", "Running", "Pending"));

        Assert.Contains("Step 2, “step 2”, runs again from its start", warning);
        Assert.Contains("may be done twice", warning);
        Assert.Contains("2 step(s) are left", warning);
    }

    // ── acting on the history (phase 2: everything the window does around a run) ─

    private ServerHistory HistoryOf() => HistoryOf(out _);

    /// <summary>The history of a service whose settings name these workers (id and role), and the engine it reads and writes.</summary>
    private ServerHistory HistoryOf(out ServerEngine engine, params (string Id, string Role)[] workers)
    {
        if (workers.Length > 0)
        {
            var settings = new Enactive.Settings.AppSettings();
            foreach (var (id, role) in workers)
                settings.Workers.Add(new Enactive.Settings.WorkerConfig { Id = id, Role = role });
            Directory.CreateDirectory(Path.Combine(_root, "data"));
            settings.Save(Path.Combine(_root, "data", "settings.json"), replaceUnreadable: true);
        }
        engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        engine.Workspaces.Touch(_workspace);
        return new ServerHistory(_users, engine, _control, new KnownWorkspaces(engine, new WorkspaceRoots([_root])));
    }

    private Enactive.Core.History.RunRecord Recorded(string request, params string[] artifacts)
        => RecordedAt(DateTimeOffset.Now, request, artifacts);

    private Enactive.Core.History.RunRecord RecordedAt(DateTimeOffset at, string request, params string[] artifacts)
    {
        var record = new Enactive.Core.History.RunRecord(Guid.NewGuid(), Guid.NewGuid(), request, "qwen", at, at.AddSeconds(3), "Completed",
            [new Enactive.Core.History.RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent",
                Payload: WorkEventPayload.RequestPayload(request))], artifacts, []);
        Enactive.Workspace.RunStoreFactory.Create(Enactive.Core.Context.WorkspaceInfo.For(_workspace))
            .SaveAsync(record, CancellationToken.None).GetAwaiter().GetResult();
        return record;
    }

    /// <summary>Try again: the same request, as another attempt at the same task, a run of whoever asked.</summary>
    [Fact]
    public async Task A_past_run_is_tried_again_as_the_same_task()
    {
        var past = Recorded("summarise the inbox");

        var (said, started) = await HistoryOf().DoAsync(As("bob"), _workspace, ServerHistory.Act.Retry, past.RunId, null, null, CancellationToken.None);

        var run = _control.Find(As("bob"), started!.Value)!;
        Assert.Equal("", said);
        Assert.Equal(("summarise the inbox", past.TaskId, "bob"), (run.Prompt, run.TaskId!.Value, run.Owner));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "bob", Action: "run tried again" });
        await _engine.ReleaseNextAsync();
    }

    /// <summary>Deleting asks first, in the window's words, and says the files stay; then the record goes and is audited.</summary>
    [Fact]
    public async Task A_run_is_deleted_after_its_question_and_its_files_stay()
    {
        File.WriteAllText(Path.Combine(_workspace, "digest.md"), "work");
        var past = Recorded("write the digest", "digest.md");
        var history = HistoryOf();

        var (question, _) = await history.AskAsync(As("alice"), _workspace, ServerHistory.Act.DeleteRun, past.RunId, null, CancellationToken.None);
        Assert.Contains("stay in your workspace", question!.Body);

        await history.DoAsync(As("alice"), _workspace, ServerHistory.Act.DeleteRun, past.RunId, null, "confirm", CancellationToken.None);

        Assert.Empty((await history.ListAsync(As("alice"), _workspace, CancellationToken.None)).List!.Runs);
        Assert.True(File.Exists(Path.Combine(_workspace, "digest.md")));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "run deleted" });
    }

    [Fact]
    public async Task A_viewer_can_neither_ask_nor_act()
    {
        var past = Recorded("write the digest");
        var history = HistoryOf();

        Assert.Contains("role does not include", (await history.AskAsync(As("vera"), _workspace, ServerHistory.Act.DeleteRun, past.RunId, null, CancellationToken.None)).Problem);
        Assert.Contains("role does not include", (await history.DoAsync(As("vera"), _workspace, ServerHistory.Act.DeleteRun, past.RunId, null, "confirm", CancellationToken.None)).Said);
        Assert.Single((await history.ListAsync(As("vera"), _workspace, CancellationToken.None)).List!.Runs);
    }

    [Fact]
    public async Task An_unfinished_run_is_forgotten_and_its_offer_to_resume_goes()
    {
        var checkpoint = Interrupted("Done", "Running");
        var history = HistoryOf();

        var (question, _) = await history.AskAsync(As("alice"), _workspace, ServerHistory.Act.Forget, checkpoint.RunId, null, CancellationToken.None);
        Assert.Contains("can no longer be resumed", question!.Body);
        await history.DoAsync(As("alice"), _workspace, ServerHistory.Act.Forget, checkpoint.RunId, null, "confirm", CancellationToken.None);

        Assert.Empty((await history.ListAsync(As("alice"), _workspace, CancellationToken.None)).List!.Resumable);
    }

    /// <summary>A file is deleted only from inside the workspace, whatever path the post names.</summary>
    [Fact]
    public async Task A_file_is_deleted_from_the_workspace_and_nowhere_else()
    {
        File.WriteAllText(Path.Combine(_workspace, "draft.md"), "x");
        File.WriteAllText(Path.Combine(_root, "outside.md"), "not the workspace's");
        var history = HistoryOf();

        Assert.Equal(("deleted", true), history.DeleteFile(As("alice"), _workspace, "draft.md"));
        var (said, _) = history.DeleteFile(As("alice"), _workspace, "..\\outside.md");

        Assert.False(File.Exists(Path.Combine(_workspace, "draft.md")));
        Assert.True(File.Exists(Path.Combine(_root, "outside.md")));
        Assert.Contains("not in this workspace", said);
    }

    /// <summary>
    /// A file is deleted by an Operator only, whatever a page sent - a Viewer is refused and the card is not done with -
    /// and an Operator's delete is audited with who did it. One way to delete a file: the history's actions
    /// (<see cref="ServerHistory.DoAsync"/>) do not, so there is no second path that could come to check less.
    /// </summary>
    [Fact]
    public async Task A_viewer_cannot_delete_a_file_and_an_operator_s_delete_is_audited()
    {
        File.WriteAllText(Path.Combine(_workspace, "draft.md"), "x");
        var history = HistoryOf();

        Assert.Equal(("Your role does not include acting on runs.", false), history.DeleteFile(As("vera"), _workspace, "draft.md"));
        Assert.True(File.Exists(Path.Combine(_workspace, "draft.md")));
        Assert.Equal("That is not something the history does.",
            (await history.DoAsync(As("alice"), _workspace, ServerHistory.Act.DeleteFile, Guid.Empty, "draft.md", "confirm", CancellationToken.None)).Said);
        Assert.True(File.Exists(Path.Combine(_workspace, "draft.md")));

        history.DeleteFile(As("alice"), _workspace, "draft.md");

        Assert.False(File.Exists(Path.Combine(_workspace, "draft.md")));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "file deleted" } && e.Detail == $"{_workspace}: draft.md (deleted)");
        Assert.DoesNotContain(_users.AuditTrail(), e => e is { Account: "vera" });
    }

    // ── a workspace's run settings (the window's slider, worker box and staging switch) ─

    /// <summary>Saved as the window saves them, audited with who did it, and what the next browser run reads (EngineExecutor).</summary>
    [Fact]
    public void An_operator_saves_a_workspace_s_level_worker_and_staging_and_it_is_audited()
    {
        var history = HistoryOf(out var engine, ("writer", "Writer"), ("coder", "Coder"));

        Assert.Null(history.SaveSettings(As("alice"), _workspace, 3, "coder", true));

        // The entry the executor reads for the next request: its Defaults and its staging.
        var saved = engine.Workspaces.Find(_workspace)!;
        Assert.Equal(3, saved.Defaults.Autonomy);
        Assert.Equal("coder", saved.Defaults.WorkerId);
        Assert.True(saved.StageChanges);
        // ... and it is on disk, for a service that starts again.
        var reloaded = Enactive.Workspace.WorkspaceRegistry.Load(filePath: Path.Combine(_root, "data", "workspaces.json")).Find(_workspace)!;
        Assert.Equal((3, "coder", true), (reloaded.Autonomy, reloaded.WorkerId, reloaded.StageChanges));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "workspace settings changed" }
            && e.Detail == $"{_workspace}: autonomous, worker coder, staging on");
    }

    /// <summary>No worker named is the workspace's default worker; and the picker offers the settings' workers.</summary>
    [Fact]
    public void No_worker_named_is_the_default_worker_and_the_picker_lists_the_settings_workers()
    {
        var history = HistoryOf(out var engine, ("writer", "Writer"), ("coder", "Coder"));
        history.SaveSettings(As("alice"), _workspace, 3, "coder", true);

        Assert.Null(history.SaveSettings(As("alice"), _workspace, 1, "  ", false));

        var saved = engine.Workspaces.Find(_workspace)!;
        Assert.Equal((1, null, false), (saved.Autonomy, saved.WorkerId, saved.StageChanges));
        Assert.Contains(_users.AuditTrail(), e => e.Detail == $"{_workspace}: suggest, worker default, staging off");
        Assert.Equal([("writer", "Writer"), ("coder", "Coder")], history.Workers());
    }

    /// <summary>The window saved a worker's role name until 2026-10-08; such a folder must save again, and keep the id.</summary>
    [Fact]
    public void A_worker_named_by_its_role_is_saved_by_its_id()
    {
        var history = HistoryOf(out var engine, ("w-1", "Writer"), ("w-2", "Code Reviewer"));

        Assert.Null(history.SaveSettings(As("alice"), _workspace, 2, " code reviewer ", false));
        Assert.Equal("w-2", engine.Workspaces.Find(_workspace)!.WorkerId);

        Assert.Null(history.SaveSettings(As("alice"), _workspace, 2, "Writer", false));
        Assert.Equal("w-1", engine.Workspaces.Find(_workspace)!.WorkerId);
        Assert.Contains(_users.AuditTrail(), e => e.Detail == $"{_workspace}: execute, worker w-1, staging off");
    }

    [Fact]
    public void A_viewer_cannot_change_a_workspace_s_settings()
    {
        var history = HistoryOf(out var engine);

        Assert.Contains("role does not include", history.SaveSettings(As("vera"), _workspace, 3, null, true));

        var saved = engine.Workspaces.Find(_workspace)!;
        Assert.Equal((2, false), (saved.Autonomy, saved.StageChanges));
        Assert.DoesNotContain(_users.AuditTrail(), e => e.Action == "workspace settings changed");
    }

    /// <summary>A page posting 9 would otherwise be saved as a level that is no position of the slider.</summary>
    [Fact]
    public void A_level_out_of_range_is_refused()
    {
        var history = HistoryOf(out var engine);

        Assert.Contains("level", history.SaveSettings(As("alice"), _workspace, 4, null, false));
        Assert.Contains("level", history.SaveSettings(As("alice"), _workspace, -1, null, false));

        Assert.Equal(2, engine.Workspaces.Find(_workspace)!.Autonomy);
    }

    [Fact]
    public void A_worker_the_settings_do_not_have_is_refused()
    {
        var history = HistoryOf(out var engine, ("writer", "Writer"));

        Assert.Contains("no worker", history.SaveSettings(As("alice"), _workspace, 2, "ghost", false));

        Assert.Null(engine.Workspaces.Find(_workspace)!.WorkerId);
    }

    /// <summary>SaveSettings alone would list any folder it is given - the server's disks, for a post.</summary>
    [Fact]
    public void Settings_of_a_folder_the_service_does_not_know_are_refused()
    {
        var history = HistoryOf(out var engine);
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.Contains("does not know that folder", history.SaveSettings(As("alice"), elsewhere, 3, null, false));

        Assert.Null(engine.Workspaces.Find(elsewhere));
    }

    // ── the service's scheduler (phase 3) ──────────────────────────────────

    /// <summary>
    /// A due schedule becomes a run in the same line as everybody's: nobody watches it, so its question is refused at
    /// once rather than waited on, and its schedule and folder are let go when it ends.
    /// </summary>
    [Fact]
    public async Task A_due_schedule_runs_in_the_line_unwatched_and_lets_go_when_it_ends()
    {
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        var policy = new PermissionPolicy(PermissionLevel.Autonomous, ["*"], []);
        var spec = new Enactive.Core.Templates.ResolvedTaskSpec("digest", 1, "Digest", Guid.NewGuid(), "project", _workspace,
            "write the digest", new Dictionary<string, string>(), policy, [], Enactive.Core.Templates.ExecutionLimits.None, null, false);
        var schedule = new Enactive.Core.Schedules.Schedule(Guid.NewGuid(), _workspace, "morning digest",
            Enactive.Core.Schedules.ScheduledWork.FromPastRun(spec.Snapshot()),
            Enactive.Core.Schedules.ScheduleTiming.Once(DateTimeOffset.Now.AddMinutes(-1)), policy, CreatedAt: DateTimeOffset.Now.AddHours(-1));
        engine.Schedules.Save(schedule);
        _engine.Ask = true;
        var scheduler = new ServerScheduler(engine, _control, NullLogger<ServerScheduler>.Instance);

        var run = Assert.Single(await scheduler.TickAsync(DateTimeOffset.Now, CancellationToken.None));

        Assert.Equal(("schedule: morning digest", "write the digest"), (run.Owner, run.Prompt));
        await WaitUntil(() => _engine.Answers.Count == 1);
        Assert.NotEqual("allow", _engine.Answers[0]);
        Assert.True(engine.Markers.IsRunning(schedule.Id));

        await _engine.ReleaseNextAsync();
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(engine.Markers.IsRunning(schedule.Id));
        Assert.Empty(await scheduler.TickAsync(DateTimeOffset.Now, CancellationToken.None));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "(scheduler)", Action: "scheduled run started" });
    }

    /// <summary>
    /// Every tick stamps the service's own pulse, beside its schedules, whether anything was due or not: the schedules page
    /// reads it to say whether anything asks them, and the machine's pulse is a console tick's, which the service is not.
    /// </summary>
    [Fact]
    public async Task A_tick_with_nothing_due_still_stamps_the_service_s_pulse()
    {
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        var now = DateTimeOffset.Now;

        Assert.Empty(await new ServerScheduler(engine, _control, NullLogger<ServerScheduler>.Instance).TickAsync(now, CancellationToken.None));

        Assert.Equal(Path.Combine(_root, "data", "tick.txt"), engine.TickFile);
        Assert.Equal(now, Enactive.Core.Schedules.Heartbeat.LastSeen(engine.TickFile));
    }

    /// <summary>Checked at the action: a folder outside the service's roots is refused whatever page asked for it.</summary>
    [Fact]
    public void A_run_outside_the_service_s_roots_is_refused()
    {
        var elsewhere = Path.Combine(_root, "roots-only-here");
        Directory.CreateDirectory(elsewhere);
        var control = new RunControl(_users, new RunQueue(2), _engine, NullLogger<RunControl>.Instance, roots: new WorkspaceRoots([elsewhere]));

        Assert.Contains("is not under", control.Start(As("alice"), _workspace, "write the digest").Refusal);
    }

    /// <summary>
    /// The roots come before anything about the folder is said or read. A folder outside them that is not there was
    /// answered "No such folder", and one that is was not - which told whoever could start a run what was on the server's
    /// disks; resuming or carrying on read the folder's checkpoints before any check at all.
    /// </summary>
    [Fact]
    public async Task A_folder_outside_the_roots_is_refused_before_anything_about_it_is_read()
    {
        var roots = Path.Combine(_root, "roots-only-here");
        Directory.CreateDirectory(roots);
        var control = new RunControl(_users, new RunQueue(2), _engine, NullLogger<RunControl>.Instance, roots: new WorkspaceRoots([roots]));

        Assert.Contains("is not under", control.Start(As("alice"), Path.Combine(_root, "not-there"), "write the digest").Refusal);
        Assert.Contains("is not under", control.Refusal(As("alice"), Path.Combine(_root, "not-there")));
        Assert.Contains("is not under", (await control.ResumeAsync(As("alice"), _workspace, Guid.NewGuid())).Refusal);
        var (parked, _) = ParkedInTheInbox();
        Assert.Contains("is not under", (await control.ContinueAsync(As("alice"), _workspace, parked, "mail the digest")).Refusal);
        Assert.Empty(control.List(As("alice")));
    }

    /// <summary>
    /// A schedule's folder is held to the roots as a person's is. The schedules file is the service account's, which its
    /// desktop and a console tick also write; a schedule there for a folder outside the roots ran an agent there every time
    /// it came due, where a run asked for in the browser was refused.
    /// </summary>
    [Fact]
    public async Task A_schedule_for_a_folder_outside_the_roots_does_not_run_and_says_so()
    {
        var elsewhere = Path.Combine(_root, "roots-only-here");
        Directory.CreateDirectory(elsewhere);
        var control = new RunControl(_users, new RunQueue(2), _engine, NullLogger<RunControl>.Instance, roots: new WorkspaceRoots([elsewhere]));
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        var policy = new PermissionPolicy(PermissionLevel.Autonomous, ["*"], []);
        var spec = new Enactive.Core.Templates.ResolvedTaskSpec("digest", 1, "Digest", Guid.NewGuid(), "project", _workspace,
            "write the digest", new Dictionary<string, string>(), policy, [], Enactive.Core.Templates.ExecutionLimits.None, null, false);
        var schedule = new Enactive.Core.Schedules.Schedule(Guid.NewGuid(), _workspace, "morning digest",
            Enactive.Core.Schedules.ScheduledWork.FromPastRun(spec.Snapshot()),
            Enactive.Core.Schedules.ScheduleTiming.Once(DateTimeOffset.Now.AddMinutes(-1)), policy, CreatedAt: DateTimeOffset.Now.AddHours(-1));
        engine.Schedules.Save(schedule);

        Assert.Empty(await new ServerScheduler(engine, control, NullLogger<ServerScheduler>.Instance).TickAsync(DateTimeOffset.Now, CancellationToken.None));

        Assert.False(engine.Markers.IsRunning(schedule.Id));
        var item = Assert.Single(await Enactive.Workspace.InboxStoreFactory.Create(Enactive.Core.Context.WorkspaceInfo.For(_workspace))
            .LoadAllAsync(CancellationToken.None));
        Assert.Contains("is not under", item.Summary);
    }

    /// <summary>
    /// A schedule's run the composer refused - its workspace held by another process - says so in the inbox, as one
    /// that could not start does. Its occurrence is spent, and nothing but a warning in the log said it did not run.
    /// </summary>
    [Fact]
    public async Task A_schedule_s_run_refused_as_it_starts_says_so_in_the_inbox()
    {
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        var policy = new PermissionPolicy(PermissionLevel.Autonomous, ["*"], []);
        var spec = new Enactive.Core.Templates.ResolvedTaskSpec("digest", 1, "Digest", Guid.NewGuid(), "project", _workspace,
            "write the digest", new Dictionary<string, string>(), policy, [], Enactive.Core.Templates.ExecutionLimits.None, null, false);
        engine.Schedules.Save(new Enactive.Core.Schedules.Schedule(Guid.NewGuid(), _workspace, "morning digest",
            Enactive.Core.Schedules.ScheduledWork.FromPastRun(spec.Snapshot()),
            Enactive.Core.Schedules.ScheduleTiming.Once(DateTimeOffset.Now.AddMinutes(-1)), policy, CreatedAt: DateTimeOffset.Now.AddHours(-1)));
        _engine.Refuse = "The workspace is in use by another process.";

        var run = Assert.Single(await new ServerScheduler(engine, _control, NullLogger<ServerScheduler>.Instance).TickAsync(DateTimeOffset.Now, CancellationToken.None));
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        var item = Assert.Single(await Enactive.Workspace.InboxStoreFactory.Create(Enactive.Core.Context.WorkspaceInfo.For(_workspace))
            .LoadAllAsync(CancellationToken.None));
        Assert.Contains("in use by another process", item.Summary);
    }

    /// <summary>
    /// The answer is spent only on a run that can carry on. The question was answered first and the run refused after
    /// (here: its folder outside the roots), and the question was gone from the inbox with nothing carried on.
    /// </summary>
    [Fact]
    public async Task A_question_whose_run_cannot_carry_on_stays_waiting_in_the_inbox()
    {
        var (_, itemId) = ParkedInTheInbox();
        var elsewhere = Path.Combine(_root, "roots-only-here");
        Directory.CreateDirectory(elsewhere);
        var control = new RunControl(_users, new RunQueue(2), _engine, NullLogger<RunControl>.Instance, roots: new WorkspaceRoots([elsewhere]));
        var inbox = InboxOf(control);

        var (said, carried) = await inbox.AnswerAsync(As("bob"), _workspace, itemId, "allow", CancellationToken.None);

        Assert.Null(carried);
        Assert.Contains("is not under", said);
        Assert.NotNull(Assert.Single(await inbox.ListAsync(As("bob"), CancellationToken.None)).Waiting);
    }

    // ── in the background (the window's "Run in background") ───────────────

    private async Task<IReadOnlyList<Enactive.Core.Inbox.InboxItem>> InboxItems()
        => await Enactive.Workspace.InboxStoreFactory.Create(Enactive.Core.Context.WorkspaceInfo.For(_workspace))
            .LoadAllAsync(CancellationToken.None);

    /// <summary>
    /// Nobody is watching a background run, so nobody is asked: its question is parked for the inbox at once (as the
    /// window's BackgroundRun parks it) rather than waiting on a page nobody has open. It is still a run of the service -
    /// listed, with its session for a page to open - and its start is audited as one in the background.
    /// </summary>
    [Fact]
    public async Task A_background_run_asks_nobody_and_parks_its_question()
    {
        _engine.Ask = true;
        _engine.StopAtAParkedQuestion = true;

        var (run, refusal) = _control.StartBackground(As("alice"), _workspace, "write the digest");

        Assert.Null(refusal);
        Assert.True(run!.Background);
        Assert.Contains(_control.List(As("vera")), r => r.Id == run.Id);
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(typeof(Enactive.Agents.ParkingDecisionHandler), Assert.Single(_engine.Handlers));
        Assert.Empty(_engine.Answers);
        Assert.Null(run.Questions.Pending);
        Assert.Null(run.Refused);
        var item = Assert.Single(await InboxItems());
        Assert.Equal("decision", item.Kind);
        Assert.Contains("Approve write_file", item.Summary);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "run started in the background" });
    }

    /// <summary>
    /// Nobody watches a background run, so its owner's plain "Allow (workspace)" - given for runs somebody watches - does
    /// not answer its question: it is parked (ApprovalStore, rule 4). It was composed as a browser run, which the composer
    /// reads as watched, and the plain approval answered. Shown on the request the executor composes and the composer's
    /// own reading of it, the engine itself being replaced by a script here.
    /// </summary>
    [Fact]
    public async Task A_background_run_is_one_nobody_watches_and_a_plain_approval_does_not_answer_it()
    {
        var run = _control.StartBackground(As("alice"), _workspace, "write the digest").Run!;
        var foreground = _control.Start(As("alice"), _workspace, "write the index").Run!;
        var saved = new Enactive.Workspace.WorkspaceEntry("project", _workspace, DateTimeOffset.Now);
        var workspace = Enactive.Core.Context.WorkspaceInfo.For(_workspace);
        var store = new ApprovalStore(Path.Combine(_root, "approvals.json"));
        store.Approve(_workspace, "write_file", principal: "alice");

        var request = EngineExecutor.RequestFor(run, workspace, saved, null);
        var watchedRequest = EngineExecutor.RequestFor(foreground, workspace, saved, null);

        Assert.Equal((Enactive.Core.Intents.IntentSource.Inbox, "alice"), (request.Source, request.Principal));
        Assert.False(Enactive.Settings.RunComposer.Attended(request));
        Assert.Null(Enactive.Settings.RunComposer.Refusal(request));
        var question = new DecisionRequest(Guid.NewGuid(), "Approve write_file", "digest.md",
            [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "write_file",
            Action: new(Guid.NewGuid(), Guid.NewGuid().ToString(), "write_file", "{}", _workspace));
        IDecisionHandler Answering(Enactive.Settings.RunRequest r) => new RememberedApprovals(new Enactive.Agents.ParkingDecisionHandler(),
            _workspace, store, null, watched: Enactive.Settings.RunComposer.Attended(r), r.Principal);
        await Assert.ThrowsAsync<Enactive.Agents.DecisionPendingException>(() => Answering(request).RequestAsync(question, CancellationToken.None));
        // The same approval answers the same question for a run somebody watches - the difference is the run, not the test.
        Assert.Equal("allow", (await Answering(watchedRequest).RequestAsync(question, CancellationToken.None)).OptionId);

        await _engine.ReleaseNextAsync();
        await _engine.ReleaseNextAsync();
    }

    /// <summary>
    /// A folder that stages cannot have a run nobody watches - it would write to the files while the history said the
    /// changes were staged. Refused as it is asked for, in the composer's words, as the window refuses it; never staged.
    /// </summary>
    [Fact]
    public void A_background_run_in_a_folder_that_stages_is_refused_in_the_composer_s_words()
    {
        var engine = new ServerEngine(Path.Combine(_root, "data"), new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        engine.Workspaces.Touch(_workspace);
        engine.Workspaces.SaveSettings(_workspace, 2, null, true);
        var executor = new EngineExecutor(engine, new ServerStaging(_users, new KnownWorkspaces(engine, new WorkspaceRoots([_root]))));

        var refusal = executor.BackgroundRefusal(_workspace, "alice");

        Assert.Contains("a run nobody is watching cannot stage", refusal);
        Assert.Null(executor.BackgroundRefusal(Path.Combine(_root, "never-seen"), "alice"));
        var staged = engine.Workspaces.Find(_workspace)!;
        _engine.RefuseBackground = refusal;
        var (run, said) = _control.StartBackground(As("alice"), _workspace, "write the digest");
        Assert.Null(run);
        Assert.Equal(refusal, said);
        Assert.Empty(_control.List(As("alice")));
        // A run that got through anyway - the folder set to stage after it started - keeps no proposals: it is refused
        // as it is composed, in the same words.
        var started = new ServerRun("alice", _workspace, "write the digest", new RunQueue(1).Enqueue(_workspace)) { Background = true };
        Assert.False(EngineExecutor.KeepsProposals(started, staged));
        Assert.Equal(refusal, Enactive.Settings.RunComposer.Refusal(
            EngineExecutor.RequestFor(started, Enactive.Core.Context.WorkspaceInfo.For(_workspace), staged, null)));
    }

    /// <summary>
    /// Something that fails after the run's events - keeping its proposals, closing its tools - does not make a run that
    /// did its work read "Failed to start" in the inbox.
    /// </summary>
    [Fact]
    public async Task A_background_run_that_ran_does_not_say_it_failed_to_start()
    {
        _engine.ThrowAfterDrain = "The proposals could not be kept.";

        var run = _control.StartBackground(As("alice"), _workspace, "write the digest").Run!;
        await _engine.ReleaseNextAsync();
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        var item = Assert.Single(await InboxItems());
        Assert.Equal(("result", "Completed · 0 artifact(s)"), (item.Kind, item.Summary));
    }

    /// <summary>
    /// However it ends, a background run leaves exactly one inbox item - the only place its end can be read. Ending at a
    /// question, it must not leave two: the one every browser run that stops at a question leaves, and its own.
    /// </summary>
    [Fact]
    public async Task A_background_run_ends_in_one_inbox_item()
    {
        _engine.EndWith = RunOutcomeKind.NeedsUser;

        var run = _control.StartBackground(As("alice"), _workspace, "write the digest").Run!;
        await _engine.ReleaseNextAsync();
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        var item = Assert.Single(await InboxItems());
        Assert.Equal(("decision", RunState.Title(run), run.Session.RunId), (item.Kind, item.Title, (Guid?)item.RunId));
        Assert.StartsWith("Waiting for your decision", item.Summary);
    }

    /// <summary>A background run that finished says how, in the same words the window's background runs use (InboxLines).</summary>
    [Fact]
    public async Task A_finished_background_run_says_so_in_the_inbox()
    {
        var run = _control.StartBackground(As("alice"), _workspace, "write the digest").Run!;
        await _engine.ReleaseNextAsync();
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        var item = Assert.Single(await InboxItems());
        Assert.Equal(("result", "Completed · 0 artifact(s)"), (item.Kind, item.Summary));
    }

    /// <summary>One that never started - refused as it was composed, cancelled in the line - says so where it is looked for.</summary>
    [Fact]
    public async Task A_background_run_that_never_started_says_so_in_the_inbox()
    {
        _engine.Refuse = "The workspace is in use by another process.";
        var refused = _control.StartBackground(As("alice"), _workspace, "write the digest").Run!;
        await refused.Ended.WaitAsync(TimeSpan.FromSeconds(10));
        var item = Assert.Single(await InboxItems());
        Assert.Equal(("error", "Failed to start: The workspace is in use by another process."), (item.Kind, item.Summary));

        _engine.Refuse = null;
        var first = _control.Start(As("bob"), _workspace, "write the digest").Run!;
        var waiting = _control.StartBackground(As("alice"), _workspace, "write the index").Run!;
        await WaitUntil(() => waiting.Waiting is not null);
        Assert.Null(_control.Cancel(As("alice"), waiting.Id));
        await waiting.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelled = (await InboxItems()).Single(i => i.Id != item.Id);
        Assert.Equal("error", cancelled.Kind);
        Assert.StartsWith("Cancelled", cancelled.Summary);
        await _engine.ReleaseNextAsync();
        await first.Ended.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A browser run stopped at a question nobody answered leaves the question in the inbox, where it is answered.</summary>
    [Fact]
    public async Task A_run_from_the_browser_stopped_at_a_question_leaves_it_in_the_inbox()
    {
        _engine.EndWith = RunOutcomeKind.NeedsUser;

        var run = _control.Start(As("alice"), _workspace, "write the digest").Run!;
        await _engine.ReleaseNextAsync();
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        var item = Assert.Single(await InboxItems());
        Assert.Equal(("decision", "Waiting for your decision"), (item.Kind, item.Summary));
    }

    [Fact]
    public void A_viewer_cannot_start_in_the_background()
    {
        var (run, refusal) = _control.StartBackground(As("vera"), _workspace, "write the digest");

        Assert.Null(run);
        Assert.Contains("role does not include", refusal);
        Assert.Empty(_control.List(As("vera")));
    }

    // ── "Delete these N": the runs a filter shows ──────────────────────────

    /// <summary>
    /// The runs the filter shows and no others - chosen by the window's own rule (RunHousekeeping), each audited by
    /// name: "these" is where the person's and the program's must agree.
    /// </summary>
    [Fact]
    public async Task Delete_these_forgets_exactly_the_runs_the_filter_shows()
    {
        var now = DateTimeOffset.Now;
        var old = RecordedAt(now.AddDays(-10), "last month's digest");
        var yesterday = RecordedAt(now.AddDays(-1), "yesterday's digest");
        var today = Recorded("today's digest");
        var history = HistoryOf();

        var shown = RunHousekeeping.Where((await history.ListAsync(As("alice"), _workspace, CancellationToken.None)).List!.Runs,
            RunFilter.OlderThanAWeek, now).Select(r => r.RunId).ToList();

        var (said, deleted) = await history.DeleteShownAsync(As("alice"), _workspace, shown, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Contains("1 run", said);
        var left = (await history.ListAsync(As("alice"), _workspace, CancellationToken.None)).List!.Runs.Select(r => r.RunId);
        Assert.Equal(new[] { yesterday.RunId, today.RunId }.Order(), left.Order());
        var audited = Assert.Single(_users.AuditTrail(), e => e.Action == "run deleted");
        Assert.Equal("alice", audited.Account);
        Assert.Contains("last month's digest", audited.Detail);
    }

    /// <summary>
    /// A run that ended between showing the list and confirming is in the filter now, but nobody saw it or counted it:
    /// it stays. Deleted are the runs the page showed, still there.
    /// </summary>
    [Fact]
    public async Task Delete_these_leaves_a_run_the_page_did_not_show()
    {
        var first = Recorded("this morning's digest");
        var second = Recorded("this afternoon's digest");
        var history = HistoryOf();
        var shown = (await history.ListAsync(As("alice"), _workspace, CancellationToken.None)).List!.Runs.Select(r => r.RunId).ToList();
        var later = Recorded("the run that ended meanwhile");

        var (_, deleted) = await history.DeleteShownAsync(As("alice"), _workspace, [.. shown, Guid.NewGuid()], CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(later.RunId, Assert.Single((await history.ListAsync(As("alice"), _workspace, CancellationToken.None)).List!.Runs).RunId);
        Assert.Equal(2, _users.AuditTrail().Count(e => e.Action == "run deleted"));
        Assert.DoesNotContain(_users.AuditTrail(), e => e.Detail?.Contains("meanwhile") == true);
        _ = (first, second);
    }

    /// <summary>
    /// After a bulk delete that could not do them all, only what the history no longer has is gone. A history that could
    /// not be read again lists nothing, and every run asked for read as gone - and was audited as deleted.
    /// </summary>
    [Fact]
    public void Runs_are_confirmed_gone_only_by_a_history_that_could_be_read()
    {
        var at = DateTimeOffset.Now;
        RunSummary Summary(string request) => new(Guid.NewGuid(), Guid.NewGuid(), request, "qwen", at, at, "Completed", [], []);
        var (a, b) = (Summary("a"), Summary("b"));

        Assert.Null(ServerHistory.ConfirmedGone([a, b], new HistoryList([], [], "The store could not be reached.")));
        Assert.Equal([a.RunId], ServerHistory.ConfirmedGone([a, b], new HistoryList([], [b], null))!.Select(r => r.RunId));
    }

    [Fact]
    public async Task A_viewer_cannot_delete_runs()
    {
        RecordedAt(DateTimeOffset.Now.AddDays(-10), "last month's digest");
        var history = HistoryOf();

        var shown = (await history.ListAsync(As("vera"), _workspace, CancellationToken.None)).List!.Runs.Select(r => r.RunId).ToList();

        var (said, deleted) = await history.DeleteShownAsync(As("vera"), _workspace, shown, CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Contains("role does not include", said);
        Assert.Single((await history.ListAsync(As("vera"), _workspace, CancellationToken.None)).List!.Runs);
        Assert.DoesNotContain(_users.AuditTrail(), e => e.Action == "run deleted");
    }
}

