namespace Enactive.Server.Tests;

using System.Threading.Channels;
using Enactive.Core.Events;
using Enactive.Server.Runs;

/// <summary>
/// A run owned by the service, not by a page (server plan 1.4): the engine is never held up by somebody watching, a
/// watcher who cannot keep up is dropped and told so, and one who comes back reads where the run stands and catches up.
/// </summary>
public sealed class RunSessionTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UtcNow, kind, summary, payload);

    /// <summary>The engine's side: events written when the test says, ended when the test says.</summary>
    private sealed class Engine
    {
        private readonly Channel<WorkEvent> _events = Channel.CreateUnbounded<WorkEvent>();
        public IAsyncEnumerable<WorkEvent> Events => _events.Reader.ReadAllAsync();
        public void Emit(WorkEvent ev) => Assert.True(_events.Writer.TryWrite(ev));
        public void End() => _events.Writer.Complete();
        public void Fail(Exception ex) => _events.Writer.Complete(ex);
    }

    private static async Task<List<JournalEntry>> AllOf(RunSession.Subscription subscription)
    {
        var all = new List<JournalEntry>();
        await foreach (var entry in subscription.Events.ReadAllAsync())
            all.Add(entry);
        return all;
    }

    /// <summary>
    /// A page that subscribes as the run ends - its watchers just told, the end not yet said - is told it ended. It was
    /// added after the last watchers were told and waited for an end that never came, showing the run as it was before
    /// its last events.
    /// </summary>
    [Fact]
    public async Task A_page_that_subscribes_as_the_run_ends_is_told_it_ended()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "tidy the wiki");
        RunSession.Subscription? late = null;
        session.Ending = () => late = session.Subscribe(session.Seq);
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);

        engine.Emit(Ev(EventKind.TaskCompleted, "Completed"));
        engine.End();
        await drained;

        Assert.Empty(await AllOf(late!).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(late!.Behind);
    }

    /// <summary>
    /// A page that leaves while it is redrawing stops following without an error. The page disposed what told it it
    /// had left, then read it again on its next wait, and its listener ended in an exception nobody observed.
    /// </summary>
    [Fact]
    public async Task A_page_that_leaves_while_it_redraws_stops_following_without_an_error()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "tidy the wiki");
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);
        var redrawing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var carryOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var follower = new RunFollower(session, () => session.Seq, async () =>
        {
            redrawing.TrySetResult();
            await carryOn.Task;
        });
        follower.Start();

        engine.Emit(Ev(EventKind.StepStarted, "step 1"));
        await redrawing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        follower.Dispose();
        carryOn.SetResult();

        await follower.Following.WaitAsync(TimeSpan.FromSeconds(5));
        engine.End();
        await drained;
    }

    [Fact]
    public async Task A_watcher_gets_every_event_in_order_numbered_and_the_end()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "tidy the wiki");
        var watcher = session.Subscribe(afterSeq: 0);
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);

        engine.Emit(Ev(EventKind.StepStarted, "step 1"));
        engine.Emit(Ev(EventKind.ToolInvoked, "read_file a.md"));
        engine.Emit(Ev(EventKind.TaskCompleted, "Completed"));
        engine.End();
        await drained;

        var seen = await AllOf(watcher);
        Assert.Equal([1L, 2L, 3L], seen.Select(e => e.Seq));
        Assert.Equal(EventKind.TaskCompleted, seen[^1].Event.Kind);
        Assert.False(watcher.Behind);
        Assert.True(session.Ended.IsCompleted);
        Assert.Equal(Run, session.RunId);
    }

    /// <summary>
    /// The window's pump made the engine wait for its drawing; a tab that is asleep would hold up a run on the service.
    /// It is dropped instead - and the run, and every other watcher, go on.
    /// </summary>
    [Fact]
    public async Task A_watcher_that_does_not_read_is_dropped_and_holds_up_neither_the_run_nor_the_others()
    {
        var engine = new Engine();
        var session = new RunSession(@"C:\work", "alice", "a long reply");
        var asleep = session.Subscribe(0);
        var awake = session.Subscribe(0);
        var awakeRead = 0;
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in awake.Events.ReadAllAsync())
                Interlocked.Increment(ref awakeRead);
        });
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);

        // In batches the awake page keeps up with, three buffers' worth in all: the asleep one cannot hold them.
        var count = RunSession.SubscriberBuffer * 3;
        for (var sent = 0; sent < count;)
        {
            for (var i = 0; i < 100 && sent < count; i++, sent++)
                engine.Emit(Ev(EventKind.AssistantDelta, "x"));
            var target = sent;
            await WaitUntil(() => Volatile.Read(ref awakeRead) >= target);
        }
        engine.End();

        await drained.WaitAsync(TimeSpan.FromSeconds(10));
        await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(asleep.Behind);
        Assert.False(awake.Behind);
        Assert.Equal(count, awakeRead);
        Assert.Equal(count, session.Seq);
    }

    /// <summary>A reopened tab reads where the run stands, subscribes from there, and misses nothing after it.</summary>
    [Fact]
    public async Task A_page_that_comes_back_reads_the_fold_and_catches_up_from_where_it_read_it()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "tidy the wiki");
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);
        engine.Emit(Ev(EventKind.StepStarted, "step 1"));
        engine.Emit(Ev(EventKind.ToolInvoked, "read_file a.md"));
        await WaitUntil(() => session.Seq == 2);

        var (action, calls, at) = session.Read((feed, activity, _, seq) => (activity.CurrentAction, activity.ToolCalls, seq));
        var later = session.Subscribe(at);
        engine.Emit(Ev(EventKind.ReviewRequested, "reviewing step 1"));
        engine.End();
        await drained;

        Assert.Equal(("read_file a.md", 1, 2L), (action, calls, at));
        Assert.Equal([3L], (await AllOf(later)).Select(e => e.Seq));
    }

    /// <summary>Asked for events the journal no longer holds: told so, not handed a run with a hole in it.</summary>
    [Fact]
    public async Task A_page_that_read_too_long_ago_is_told_to_read_again()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "t", tailCapacity: 10);
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);
        for (var i = 0; i < 25; i++)
            engine.Emit(Ev(EventKind.AssistantDelta, "x"));
        engine.End();
        await drained;

        Assert.True(session.Subscribe(afterSeq: 3).Behind);
        Assert.False(session.Subscribe(afterSeq: 20).Behind);
        Assert.Equal([21L, 22L, 23L, 24L, 25L], (await AllOf(session.Subscribe(20))).Select(e => e.Seq));
    }

    [Fact]
    public async Task A_run_that_fails_says_why_and_ends_its_watchers()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "t");
        var watcher = session.Subscribe(0);
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);

        engine.Emit(Ev(EventKind.StepStarted, "step 1"));
        engine.Fail(new InvalidOperationException("the model went away"));
        await drained;

        Assert.Equal("the model went away", session.Failure);
        Assert.Single(await AllOf(watcher));
        Assert.True(session.Ended.IsCompleted);
    }

    [Fact]
    public async Task A_watcher_that_left_is_sent_nothing_more()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "t");
        var left = session.Subscribe(0);
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);

        session.Unsubscribe(left);
        engine.Emit(Ev(EventKind.StepStarted, "step 1"));
        engine.End();
        await drained;

        Assert.Empty(await AllOf(left));
    }

    /// <summary>
    /// The status column's figures - which model served each phase, the tokens and where they ran, the files, each
    /// model's speed - read with the cards under the same lock, at the same event. Without the panel in the session a
    /// page had the cards and nothing else, and would have had to fold the events a second time to show the rest.
    /// </summary>
    [Fact]
    public async Task The_panel_is_folded_with_the_feed()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "tidy the wiki",
            reachOf: id => id == "ollama" ? Enactive.Application.Projections.ModelReach.Local : Enactive.Application.Projections.ModelReach.Cloud);
        // A call can be reported before the run's first event has said the run's id: it is shown once it has.
        session.AddMetrics(new Enactive.Core.Chat.ModelCallMetrics("ollama", "qwen", 100, 20, null, 2.0, 0.5, null, Run));
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);

        engine.Emit(Ev(EventKind.Routed, "Worker -> model ollama/qwen", WorkEventPayload.RoutePayload("worker", "ollama", "qwen")));
        engine.Emit(Ev(EventKind.UsageReported, "usage", WorkEventPayload.UsagePayload(100, 20, 1, providerId: "ollama")));
        engine.Emit(Ev(EventKind.ArtifactProduced, "Wrote: wiki.md", WorkEventPayload.ArtifactPayload("document", "wiki.md")));
        engine.End();
        await drained;

        var (routes, prompt, completion, reaches, artifacts, models, seq) = session.Read((_, _, panel, at) => (
            panel.Routing.Rows.Select(r => (r.Phase, r.Model)).ToList(),
            panel.PromptTokens, panel.CompletionTokens,
            panel.Work.Rows.Select(r => r.Reach).ToList(),
            panel.Artifacts,
            panel.Performance.Rows.Select(r => r.Model).ToList(),
            at));

        Assert.Equal([("worker", "ollama/qwen")], routes);
        Assert.Equal((100, 20), (prompt, completion));
        Assert.Equal([Enactive.Application.Projections.ModelReach.Local], reaches);
        Assert.Equal(1, artifacts);
        Assert.Equal(["qwen"], models);
        Assert.Equal(3L, seq);
    }

    /// <summary>
    /// Who else has the run open, for "also watching": each person once however many tabs they have, and gone when
    /// their last tab leaves - a name kept after its page closed would say somebody is looking who is not.
    /// </summary>
    [Fact]
    public void Watchers_are_named_once_and_leave_when_they_unsubscribe()
    {
        var session = new RunSession("C:\\work", "alice", "t");
        var bobsFirstTab = session.Subscribe(0, "bob");
        var alice = session.Subscribe(0, "alice");
        var bobsSecondTab = session.Subscribe(0, "bob");
        session.Subscribe(0); // nobody named: a test's, or a page that does not say who it is for

        Assert.Equal(["alice", "bob"], session.Watchers);

        session.Unsubscribe(bobsFirstTab);
        Assert.Equal(["alice", "bob"], session.Watchers);
        session.Unsubscribe(bobsSecondTab);
        Assert.Equal(["alice"], session.Watchers);
        session.Unsubscribe(alice);
        Assert.Empty(session.Watchers);
    }

    /// <summary>A watcher dropped for falling behind, and every watcher once the run is over, is no longer watching.</summary>
    [Fact]
    public async Task Watchers_leave_when_they_fall_behind_and_when_the_run_ends()
    {
        var engine = new Engine();
        var session = new RunSession("C:\\work", "alice", "a long reply");
        var asleep = session.Subscribe(0, "asleep");
        var drained = session.DrainAsync(engine.Events, CancellationToken.None);

        for (var i = 0; i < RunSession.SubscriberBuffer + 1; i++)
            engine.Emit(Ev(EventKind.AssistantDelta, "x"));
        await WaitUntil(() => session.Seq == RunSession.SubscriberBuffer + 1);
        // Subscribed after the burst, so it is not the one that falls behind.
        session.Subscribe(session.Seq, "awake");

        Assert.True(asleep.Behind);
        Assert.Equal(["awake"], session.Watchers);

        engine.End();
        await drained;
        Assert.Empty(session.Watchers);
    }

    /// <summary>A page following the run says whose it is, and stops being counted when it leaves.</summary>
    [Fact]
    public void A_page_following_the_run_is_named_among_its_watchers()
    {
        var session = new RunSession("C:\\work", "alice", "t");
        var follower = new RunFollower(session, () => session.Seq, () => Task.CompletedTask, watcher: "bob");

        follower.Start();
        Assert.Equal(["bob"], session.Watchers);
        follower.Dispose();
        Assert.Empty(session.Watchers);
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
}
