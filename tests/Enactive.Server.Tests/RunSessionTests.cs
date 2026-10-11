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

        var (action, calls, at) = session.Read((feed, activity, seq) => (activity.CurrentAction, activity.ToolCalls, seq));
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
