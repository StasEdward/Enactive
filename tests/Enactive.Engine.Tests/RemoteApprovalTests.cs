namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// Answering a permission from somewhere else. Stage 5 of <c>Docs/REMOTE_DESIGN.md</c>.
///
/// <para>The rule the whole stage turns on: the remote handler WRAPS the desktop rather than
/// replacing it, so sitting down at the computer always works, whatever the phone is doing. And a
/// shell is never offered remotely at all - which is what keeps the sandbox plan's threat model
/// standing now that starting a task has a network origin.</para>
/// </summary>
public sealed class RemoteApprovalTests : IDisposable
{
    private const string RunId = "run-1";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "enactive-approval-" + Guid.NewGuid().ToString("N"));

    private readonly HostStore _store;
    private readonly RemoteApprovals _approvals = new();

    public RemoteApprovalTests()
    {
        _store = new HostStore(Path.Combine(_folder, "remote.db"));

        _store.Accept(new HostCommand(
            "command-1", "host-1", CommandKind.StartTask,
            RemoteJson.Serialize(new StartTaskPayload(
                RunId, "task-1", "workspace-1", "T", "P", DateTimeOffset.UtcNow)),
            CommandStatus.PendingDelivery, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24)));

        _store.BeginRun("command-1", RunId);
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Litter, not a failure.
        }
    }

    private RemoteDecisionHandler Handler(IDecisionHandler desktop, TimeSpan? timeout = null)
        => new(desktop, _store, _approvals, RunId, timeout ?? TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remote_requests_require_a_fresh_desktop_answer_even_in_the_same_workspace(bool bound)
    {
        var request = Ask(bind: bound) with { Subject = "write_file" };
        var remembered = new SessionApprovals();
        remembered.Remember(request);
        var desktop = new CheckingDesktop(seen =>
        {
            Assert.True(seen.RequiresExplicitAnswer);
            Assert.Equal(request.Id, seen.Id);
            Assert.False(remembered.Approves(seen));
            return Task.FromResult(new DecisionOutcome("deny"));
        });
        Assert.Equal("deny", (await Handler(desktop).RequestAsync(request, default)).OptionId);
        Assert.Empty(_approvals.Pending);
    }

    [Fact]
    public async Task Desktop_failure_invalidates_the_remote_question_and_clears_its_waiter()
    {
        var desktop = new CheckingDesktop(_ => throw new IOException("UI failed"));
        await Assert.ThrowsAsync<IOException>(() => Handler(desktop).RequestAsync(Ask(), default));
        Assert.Empty(_approvals.Pending);
        var resolution = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalResolved).Resolution!;
        Assert.Equal(ApprovalOutcome.Invalidated, resolution.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_answer_waits_for_desktop_cleanup_and_stop_during_cleanup_invalidates_it(bool stop)
    {
        using var cancellation = new CancellationTokenSource();
        var desktop = new CleaningDesktop();
        var deciding = Handler(desktop).RequestAsync(Ask(), cancellation.Token);
        var published = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalRequested).Request!;
        Assert.True(_approvals.TryAnswer(published.ApprovalId, published.ActionHash, RemoteDecision.Allow));
        await desktop.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(deciding.IsCompleted);
            Assert.Empty(_approvals.Pending);
            if (stop) cancellation.Cancel();
        }
        finally { desktop.Cleaned.TrySetResult(); }
        if (stop) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deciding);
        else Assert.Equal("allow", (await deciding).OptionId);
        var resolved = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalResolved).Resolution!;
        Assert.Equal(stop ? ApprovalOutcome.Invalidated : ApprovalOutcome.Allowed, resolved.Outcome);
    }

    [Fact]
    public async Task Precancelled_remote_request_never_publishes_or_asks_desktop()
    {
        var desktop = new SilentDesktop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(desktop)
            .RequestAsync(Ask(), new CancellationToken(true)));
        Assert.Empty(Queued());
        Assert.Empty(_approvals.Pending);
        Assert.False(desktop.Asked.Task.IsCompleted);
    }

    private sealed class CleaningDesktop : IDecisionHandler
    {
        public TaskCompletionSource Cleaning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleaned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); return new("allow"); }
            finally { Cleaning.TrySetResult(); await Cleaned.Task; }
        }
    }

    private sealed class CheckingDesktop(Func<DecisionRequest, Task<DecisionOutcome>> answer) : IDecisionHandler
    {
        public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct) => answer(request);
    }

    private static DecisionRequest Ask(string tool = "write_file", bool bind = true)
        => new(
            Guid.NewGuid(), $"Approve tool '{tool}'?", "short form",
            [new DecisionOption("allow", "Allow"), new DecisionOption("deny", "Deny")],
            RecommendedOptionId: "allow",
            FullDetail: "the complete action, unabridged",
            Action: bind
                ? new BoundAction(Guid.NewGuid(), "call-1", tool, """{"path":"a.txt"}""", "C:/work")
                : null);

    /// <summary>Everything queued for the run, drained.</summary>
    private (RemoteEventKind Kind, ApprovalRequest? Request, ApprovalResolution? Resolution)[] Queued()
    {
        var events = new List<(RemoteEventKind, ApprovalRequest?, ApprovalResolution?)>();

        while (_store.NextOwed().FirstOrDefault(o => o.RunId == RunId) is { } owed)
        {
            events.Add((owed.Event.Kind, owed.Event.Approval, owed.Event.Resolution));
            _store.Discard(owed.EventId);
        }

        return events.ToArray();
    }

    // ── the race ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_permission_can_be_answered_from_the_web()
    {
        var desktop = new SilentDesktop();
        var handler = Handler(desktop);
        var request = Ask();

        var deciding = handler.RequestAsync(request, CancellationToken.None);
        await desktop.Asked.Task;

        var published = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalRequested).Request!;
        Assert.True(_approvals.TryAnswer(published.ApprovalId, published.ActionHash, RemoteDecision.Allow));

        Assert.Equal("allow", (await deciding).OptionId);
    }

    /// <summary>
    /// The desktop got there first. A command carrying the owner's answer arrives afterwards and
    /// finds nothing waiting, which is the ordinary case rather than an error - and it must not be
    /// able to override what a person at the machine already decided.
    /// </summary>
    [Fact]
    public async Task A_local_answer_beats_a_queued_remote_one()
    {
        var desktop = new SilentDesktop();
        var handler = Handler(desktop);

        var deciding = handler.RequestAsync(Ask(), CancellationToken.None);
        await desktop.Asked.Task;

        var published = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalRequested).Request!;
        desktop.Answer("deny");

        Assert.Equal("deny", (await deciding).OptionId);
        Assert.False(_approvals.TryAnswer(published.ApprovalId, published.ActionHash, RemoteDecision.Allow));
    }

    /// <summary>An answer about a different action is not an answer to this one.</summary>
    [Fact]
    public async Task An_answer_carrying_the_wrong_action_hash_is_not_accepted()
    {
        var desktop = new SilentDesktop();
        var handler = Handler(desktop);

        var deciding = handler.RequestAsync(Ask(), CancellationToken.None);
        await desktop.Asked.Task;

        var published = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalRequested).Request!;

        Assert.False(_approvals.TryAnswer(published.ApprovalId, "a-different-hash", RemoteDecision.Allow));

        desktop.Answer("allow");
        await deciding;
    }

    // ── the boundary ────────────────────────────────────────────────────────

    /// <summary>
    /// A run started from the web does not run a shell, and NOBODY is asked whether it should.
    ///
    /// <para>The request is still published - the panel has to be able to show what the run wanted
    /// to do - and then refused on the spot. The desktop is not consulted at all, which is the part
    /// worth a test: an earlier version raced it, so a person at the keyboard could approve, with
    /// one click, a shell command they had not started. A leaked owner key plus a casual click is a
    /// shorter path to arbitrary command execution than a leaked owner key alone.</para>
    ///
    /// <para>Shown red by restoring that race: the desktop is asked, and answers.</para>
    /// </summary>
    [Theory]
    [InlineData("run_command")]
    [InlineData("git")]
    public async Task A_shell_in_a_remote_run_is_refused_without_asking_anybody(string tool)
    {
        var desktop = new SilentDesktop();

        var outcome = await Handler(desktop).RequestAsync(Ask(tool), CancellationToken.None);

        Assert.False(desktop.Asked.Task.IsCompleted, "the desktop was asked about a shell it should never have seen.");
        Assert.Equal("deny", outcome.OptionId);

        // Drained ONCE: Queued() empties the outbox as it reads it, so asking twice is asking an
        // empty queue the second time.
        var queued = Queued();

        var published = Assert.Single(queued, e => e.Kind == RemoteEventKind.ApprovalRequested).Request!;
        Assert.False(published.RemoteDecidable);
        Assert.Equal(tool, published.Tool);

        // Published AND resolved, in that order. The panel shows what was asked for and that it was
        // refused; a refusal nobody is shown is indistinguishable from a step that never happened.
        var resolved = Assert.Single(queued, e => e.Kind == RemoteEventKind.ApprovalResolved).Resolution!;
        Assert.Equal(ApprovalOutcome.Denied, resolved.Outcome);
        Assert.Equal(published.ApprovalId, resolved.ApprovalId);
        Assert.Equal(RemoteEventKind.ApprovalRequested, queued[0].Kind);
    }

    /// <summary>And an answer offered for it over the wire still does nothing, as before.</summary>
    [Fact]
    public async Task An_answer_offered_for_a_shell_is_not_accepted()
    {
        await Handler(new SilentDesktop()).RequestAsync(Ask("run_command"), CancellationToken.None);

        var published = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalRequested).Request!;

        Assert.False(_approvals.TryAnswer(published.ApprovalId, published.ActionHash, RemoteDecision.Allow));
    }

    /// <summary>
    /// Nothing to identify remotely means nothing to publish. A card on the phone that no answer
    /// could ever be matched to is worse than no card.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_bound_action_stays_on_the_machine()
    {
        var desktop = new SilentDesktop();
        var deciding = Handler(desktop).RequestAsync(Ask(bind: false), CancellationToken.None);

        await desktop.Asked.Task;
        Assert.Empty(Queued());

        desktop.Answer("allow");
        await deciding;
    }

    // ── the clock ───────────────────────────────────────────────────────────

    /// <summary>
    /// Nobody answered. A step that waits for ever for an answer nobody can give is a hang, not a
    /// permission model - so the request expires, says so, and the answer is no.
    /// </summary>
    [Fact]
    public async Task A_permission_nobody_answers_expires_and_is_refused()
    {
        var outcome = await Handler(new SilentDesktop(), TimeSpan.FromMilliseconds(50))
            .RequestAsync(Ask(), CancellationToken.None);

        Assert.Equal("deny", outcome.OptionId);

        var resolution = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalResolved).Resolution!;
        Assert.Equal(ApprovalOutcome.Expired, resolution.Outcome);
    }

    /// <summary>
    /// Every request reports how it ended, so the panel never leaves a card open for something that
    /// has already been settled here.
    /// </summary>
    [Fact]
    public async Task An_answered_request_reports_its_outcome()
    {
        var desktop = new SilentDesktop();
        var deciding = Handler(desktop).RequestAsync(Ask(), CancellationToken.None);

        await desktop.Asked.Task;
        desktop.Answer("allow");
        await deciding;

        var resolution = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalResolved).Resolution!;
        Assert.Equal(ApprovalOutcome.Allowed, resolution.Outcome);
    }

    /// <summary>A desktop handler that answers only when a test tells it to.</summary>
    private sealed class SilentDesktop : IDecisionHandler
    {
        private readonly TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Answer(string optionId) => _answer.TrySetResult(optionId);

        public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        {
            Asked.TrySetResult();

            await using var registration = ct.Register(() => _answer.TrySetCanceled(ct));
            return new DecisionOutcome(await _answer.Task);
        }
    }
}
