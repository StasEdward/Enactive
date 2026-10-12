namespace Enactive.Server.Runs;

/// <summary>
/// A page following a run (RunPage): it reads where the run stands, subscribes from there, and redraws as events come -
/// at most ten times a second. Dropped for falling behind, it reads again and subscribes afresh, which is also what a
/// reopened tab does. Kept out of the page so what it does when the page leaves can be tested without one.
/// </summary>
internal sealed class RunFollower(RunSession session, Func<long> read, Func<Task> redraw) : IDisposable
{
    /// <summary>A reply arrives one event per token, and a page redrawn for each is a browser kept busy for nothing.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(100);

    private readonly CancellationTokenSource _leaving = new();
    private RunSession.Subscription? _subscription;

    /// <summary>The listening, until the run ends or the page leaves; for a test to wait on.</summary>
    public Task Following { get; private set; } = Task.CompletedTask;

    /// <summary>Reads where the run stands and follows it from there.</summary>
    public void Start()
    {
        if (_leaving.IsCancellationRequested)
            return;
        _subscription = session.Subscribe(read());
        Following = ListenAsync(_subscription, _leaving.Token);
    }

    // The token, taken once and passed: read from the source after the page left, it threw - the source is disposed
    // with the page - and the listener ended in an exception nobody observed.
    private async Task ListenAsync(RunSession.Subscription subscription, CancellationToken leaving)
    {
        try
        {
            while (await subscription.Events.WaitToReadAsync(leaving))
            {
                while (subscription.Events.TryRead(out _)) { }
                read();
                await redraw();
                await Task.Delay(Throttle, leaving);
            }
            if (subscription.Behind && !leaving.IsCancellationRequested)
            {
                Start();
                return;
            }
            read();
            await redraw();
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _leaving.Cancel();
        if (_subscription is not null)
            session.Unsubscribe(_subscription);
        _leaving.Dispose();
    }
}
