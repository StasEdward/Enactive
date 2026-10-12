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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
        {
            if (Refuse is { } refusal)
                throw new InvalidOperationException(refusal);
            var events = Channel.CreateUnbounded<WorkEvent>();
            var drained = run.Session.DrainAsync(events.Reader.ReadAllAsync(ct), ct);
            var runId = Guid.NewGuid();
            WorkEvent Ev(EventKind kind, string summary) => new(Guid.NewGuid(), Guid.NewGuid(), runId, DateTimeOffset.UtcNow, kind, summary, null);

            events.Writer.TryWrite(Ev(EventKind.StepStarted, "step 1: write the digest"));
            if (Ask)
            {
                var outcome = await decisions.RequestAsync(new DecisionRequest(Guid.NewGuid(), "Approve write_file", "digest.md",
                    [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "write_file",
                    Action: new(runId, Guid.NewGuid().ToString(), "write_file", "{}", run.Workspace)), ct);
                lock (Answers) Answers.Add(outcome.OptionId);
            }
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_releases) _releases.Add(release);
            await release.Task.WaitAsync(ct);
            events.Writer.TryWrite(Ev(EventKind.TaskCompleted, "Completed"));
            events.Writer.Complete();
            await drained;
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
        return new ServerInbox(_users, engine, control);
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
        return new ServerHistory(_users, engine, _control, new WorkspaceRoots([_root]));
    }

    private Enactive.Core.History.RunRecord Recorded(string request, params string[] artifacts)
    {
        var at = DateTimeOffset.Now;
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

        await history.DoAsync(As("alice"), _workspace, ServerHistory.Act.DeleteFile, Guid.Empty, "draft.md", "confirm", CancellationToken.None);
        var (said, _) = await history.DoAsync(As("alice"), _workspace, ServerHistory.Act.DeleteFile, Guid.Empty, "..\\outside.md", "confirm", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_workspace, "draft.md")));
        Assert.True(File.Exists(Path.Combine(_root, "outside.md")));
        Assert.Contains("not in this workspace", said);
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
}

