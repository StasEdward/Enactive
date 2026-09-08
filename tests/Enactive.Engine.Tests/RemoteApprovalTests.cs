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
    /// A shell is published so the panel can say what is being asked, and marked as one the panel
    /// may not answer. An answer offered for it does nothing here - and the gateway refuses it as
    /// well. Two independent refusals, because this one alone would be a promise and that one alone
    /// would be trusting the panel.
    /// </summary>
    [Fact]
    public async Task A_shell_is_published_but_cannot_be_answered_remotely()
    {
        var desktop = new SilentDesktop();
        var handler = Handler(desktop);

        var deciding = handler.RequestAsync(Ask("run_command"), CancellationToken.None);
        await desktop.Asked.Task;

        var published = Assert.Single(Queued(), e => e.Kind == RemoteEventKind.ApprovalRequested).Request!;

        Assert.False(published.RemoteDecidable);
        Assert.Equal("run_command", published.Tool);
        Assert.False(_approvals.TryAnswer(published.ApprovalId, published.ActionHash, RemoteDecision.Allow));

        // And the machine can still answer it, which is the whole point of publishing it at all.
        desktop.Answer("allow");
        Assert.Equal("allow", (await deciding).OptionId);
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
