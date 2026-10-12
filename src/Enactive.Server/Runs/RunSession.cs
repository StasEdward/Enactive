namespace Enactive.Server.Runs;

using System.Threading.Channels;
using Enactive.Application.Projections;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>One event of a run, numbered in the order the run produced it.</summary>
internal sealed record JournalEntry(long Seq, WorkEvent Event);

/// <summary>
/// The service's owner of one run (ADR 0002 §6, server plan A2): it drains the engine's events, keeps them numbered in
/// a journal, keeps the run's fold up to date, and hands both to whoever is watching - none of whom it ever waits for.
///
/// <para><b>Why not the window's pump.</b> <c>RunEventPump</c> couples reading events to drawing them: a bounded
/// channel the engine WAITS on, and a drawing failure that cancels the run. That is right for one window and wrong for
/// a service: a browser tab that is slow, asleep or gone would hold up - or stop - a run somebody else may be relying
/// on. Here the engine is read as fast as it produces, and a watcher that cannot keep up is dropped and told so.</para>
///
/// <para><b>Watching.</b> A page reads the fold (<see cref="Read{T}"/>) - the cards, the activity - and subscribes from
/// the sequence number it read it at. It is sent every event after that, in order. One that falls more than its buffer
/// behind is dropped and marked <see cref="Subscription.Behind"/>: it reads the fold again and subscribes afresh, which
/// is also exactly what a reopened tab does. The journal keeps the newest <c>tailCapacity</c> events for that catch-up;
/// the fold, not the journal, is what holds the whole run.</para>
/// </summary>
internal sealed class RunSession
{
    /// <summary>How many events a watcher may fall behind before it is dropped. Generous: a reply arrives one event per token.</summary>
    public const int SubscriberBuffer = 1024;

    private readonly object _gate = new();
    private readonly int _tailCapacity;
    private readonly Queue<JournalEntry> _tail = new();
    private readonly List<Subscription> _watchers = new();
    private readonly RunFeed _feed;
    private readonly RunActivity _activity = new();
    private readonly RunPanel _panel;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;

    /// <summary>The events have ended and the watchers been taken to be told; under <see cref="_gate"/>.</summary>
    private bool _over;

    /// <param name="reachOf">
    /// Where a provider runs, by its id, for the panel's split of the work (ModelReaches over the service's settings);
    /// null, every provider Unknown - a run with no settings to say it (a test's, a scripted one).
    /// </param>
    public RunSession(string workspace, string owner, string title, Func<string?, ModelReach>? reachOf = null, int tailCapacity = 4096)
    {
        Workspace = workspace;
        Owner = owner;
        _feed = new RunFeed(title);
        _panel = new RunPanel(reachOf ?? (_ => ModelReach.Unknown));
        _tailCapacity = tailCapacity;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Workspace { get; }

    /// <summary>The account that started the run.</summary>
    public string Owner { get; }

    /// <summary>The engine's id for the run, once its first event has said it.</summary>
    public Guid? RunId { get; private set; }

    /// <summary>Completes when the run has ended, however it ended.</summary>
    public Task Ended => _ended.Task;

    /// <summary>How the run ended, as the engine said it; null until it has.</summary>
    public RunOutcomeKind? Outcome { get; private set; }

    /// <summary>Why the run stopped without ending properly - an error the engine threw - or null.</summary>
    public string? Failure { get; private set; }

    /// <summary>A test's: called as the run ends, its watchers taken and not yet told - where a page subscribing lands.</summary>
    internal Action? Ending { get; set; }

    /// <summary>The number of the newest event; 0 before the first.</summary>
    public long Seq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>
    /// Who has the run open now: the names given to the live subscriptions, each once however many tabs, in order. A
    /// subscription leaves it when it is unsubscribed, dropped for falling behind, or ended with the run - a name kept
    /// after its page was gone would say somebody is watching who is not.
    /// </summary>
    public IReadOnlyList<string> Watchers
    {
        get
        {
            lock (_gate)
                return _watchers.Select(w => w.Watcher).OfType<string>().Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Reads the run's fold as it stands - the cards, the activity, the status column's figures - with the number of the
    /// newest event folded in, the point to subscribe from. Under the session's lock: the fold is changed by the drain as
    /// events arrive, and a page must not read it half-way. Keep <paramref name="read"/> short; copy what is needed out
    /// of it.
    /// </summary>
    public T Read<T>(Func<RunFeed, RunActivity, RunPanel, long, T> read)
    {
        lock (_gate)
            return read(_feed, _activity, _panel, _seq);
    }

    /// <summary>
    /// A model call this run made, for the panel's speed figures: from the engine's callback, found by the run's flow
    /// (ServerRunScope). Under the lock: the call is reported on the engine's thread while a page may be reading.
    /// </summary>
    public void AddMetrics(ModelCallMetrics m)
    {
        lock (_gate)
            _panel.Add(m);
    }

    /// <summary>
    /// Drains the run's events until they end. The only thing awaited is the engine; every watcher is written to
    /// without waiting. Never throws: a run that fails says so in <see cref="Failure"/>, and its watchers are told it ended.
    /// </summary>
    public async Task DrainAsync(IAsyncEnumerable<WorkEvent> events, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in events.WithCancellation(ct).ConfigureAwait(false))
                Append(ev);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate) _feed.Stopped();
            Failure = "Cancelled.";
        }
        catch (Exception ex)
        {
            lock (_gate) _feed.Stopped();
            Failure = ex.Message;
        }
        finally
        {
            List<Subscription> watchers;
            lock (_gate)
            {
                // Over in the same breath as the watchers are taken: a page subscribing between the two was added after
                // the last ones were told, and waited for an end that never came.
                _over = true;
                watchers = [.. _watchers];
                _watchers.Clear();
            }
            Ending?.Invoke();
            foreach (var watcher in watchers)
                watcher.Complete();
            _ended.TrySetResult();
        }
    }

    private void Append(WorkEvent ev)
    {
        List<Subscription>? dropped = null;
        lock (_gate)
        {
            if (RunId is null)
            {
                RunId = ev.RunId;
                // The speed figures show one run's calls and keep others' aside (ModelPerformance): without being told
                // which run this is they showed none, and a call reported before this first event waits there till now.
                _panel.Performance.ShowRun(ev.RunId);
            }
            if (ev.Kind is EventKind.TaskCompleted or EventKind.TaskFailed)
                Outcome = ev.Outcome() ?? (ev.Kind == EventKind.TaskCompleted ? RunOutcomeKind.Completed : RunOutcomeKind.Failed);
            var entry = new JournalEntry(++_seq, ev);
            _feed.Apply(ev);
            _activity.Apply(ev);
            _panel.Apply(ev);

            _tail.Enqueue(entry);
            if (_tail.Count > _tailCapacity)
                _tail.Dequeue();

            foreach (var watcher in _watchers)
                if (!watcher.Offer(entry))
                    (dropped ??= []).Add(watcher);
            if (dropped is not null)
                _watchers.RemoveAll(dropped.Contains);
        }
    }

    /// <summary>
    /// Every event after <paramref name="afterSeq"/>, in order, as they come. When the journal no longer holds them all
    /// - the page read its fold too long ago - the subscription starts <see cref="Subscription.Behind"/>, and the page
    /// reads the fold again.
    /// </summary>
    /// <param name="watcher">Whose page it is, for <see cref="Watchers"/>; null for one that does not say.</param>
    public Subscription Subscribe(long afterSeq, string? watcher = null)
    {
        var subscription = new Subscription(watcher);
        lock (_gate)
        {
            var oldest = _tail.Count > 0 ? _tail.Peek().Seq : _seq + 1;
            if (afterSeq < oldest - 1 || afterSeq > _seq)
            {
                subscription.FellBehind();
                return subscription;
            }

            foreach (var entry in _tail)
                if (entry.Seq > afterSeq && !subscription.Offer(entry))
                    return subscription;

            if (_over)
                subscription.Complete();
            else
                _watchers.Add(subscription);
        }
        return subscription;
    }

    /// <summary>Stops sending to a watcher that has gone - a closed tab, a page navigated away.</summary>
    public void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
            _watchers.Remove(subscription);
        subscription.Complete();
    }

    /// <summary>One watcher's feed of events.</summary>
    public sealed class Subscription(string? watcher = null)
    {
        /// <summary>Whose page it is, or null.</summary>
        public string? Watcher { get; } = watcher;

        private readonly Channel<JournalEntry> _channel = Channel.CreateBounded<JournalEntry>(
            new BoundedChannelOptions(SubscriberBuffer) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        public ChannelReader<JournalEntry> Events => _channel.Reader;

        /// <summary>Dropped for falling behind, or started too late for the journal: read the fold again and subscribe afresh.</summary>
        public bool Behind { get; private set; }

        /// <summary>Written without waiting; false - and the watcher dropped - when its buffer is full.</summary>
        internal bool Offer(JournalEntry entry)
        {
            if (_channel.Writer.TryWrite(entry))
                return true;
            FellBehind();
            return false;
        }

        internal void FellBehind()
        {
            Behind = true;
            _channel.Writer.TryComplete();
        }

        internal void Complete() => _channel.Writer.TryComplete();
    }
}
