namespace Enactive.Server.Runs;

using System.Threading.Channels;
using Enactive.Application.Projections;
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
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;

    public RunSession(string workspace, string owner, string title, int tailCapacity = 4096)
    {
        Workspace = workspace;
        Owner = owner;
        _feed = new RunFeed(title);
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

    /// <summary>The number of the newest event; 0 before the first.</summary>
    public long Seq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>
    /// Reads the run's fold as it stands, with the number of the newest event folded in - the point to subscribe from.
    /// Under the session's lock: the fold is changed by the drain as events arrive, and a page must not read it half-way.
    /// Keep <paramref name="read"/> short; copy what is needed out of it.
    /// </summary>
    public T Read<T>(Func<RunFeed, RunActivity, long, T> read)
    {
        lock (_gate)
            return read(_feed, _activity, _seq);
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
                watchers = [.. _watchers];
                _watchers.Clear();
            }
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
            RunId ??= ev.RunId;
            if (ev.Kind is EventKind.TaskCompleted or EventKind.TaskFailed)
                Outcome = ev.Outcome() ?? (ev.Kind == EventKind.TaskCompleted ? RunOutcomeKind.Completed : RunOutcomeKind.Failed);
            var entry = new JournalEntry(++_seq, ev);
            _feed.Apply(ev);
            _activity.Apply(ev);

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
    public Subscription Subscribe(long afterSeq)
    {
        var subscription = new Subscription();
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

            if (_ended.Task.IsCompleted)
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
    public sealed class Subscription
    {
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
