namespace Enactive.App.Ui;

using Avalonia.Threading;

/// <summary>
/// The window's clock for a shared view model that drains a queue (LogWindowViewModel): a dispatcher timer, so each drain
/// runs on the UI thread the view model's lists are bound on. The view model lives in the application layer, which has no
/// UI toolkit; this is the window's half of its drain.
/// </summary>
internal static class DispatcherDrain
{
    /// <summary>Calls the drain every <paramref name="interval"/> on the UI thread until the handle is disposed.</summary>
    public static Func<Action, IDisposable> Every(TimeSpan interval) => drain =>
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => drain();
        timer.Start();
        return new Stop(timer);
    };

    private sealed class Stop(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
