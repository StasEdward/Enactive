namespace Enactive.Server.Tests;

using System.Security.Claims;
using System.Threading.Channels;
using Enactive.Application;
using Enactive.Core.Events;
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
        public List<string> Answers { get; } = new();
        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
        {
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
}
