namespace Enactive.Server.Runs;

/// <summary>
/// Telling the pages that something changed - a run moved, a question went up, the inbox or the proposals changed, a
/// watcher came or went. The pages listen on the service's own objects, and every open tab is a listener.
///
/// <para>A multicast event invoked as one call stops at the first listener that throws: the listeners after it were never
/// told, and the throw went up into whatever was changing - the end of a run, an applied proposal, an answer recorded -
/// which then failed half done, because one tab's page had a fault. Each listener is told on its own here; one that
/// throws is written to the log and the others are still told.</para>
/// </summary>
internal static class Listeners
{
    /// <summary>
    /// Where a listener's failure is written: the service's log, set as the service starts (ServerApp). Null - in a test
    /// that builds no service - it is only passed over.
    /// </summary>
    public static ILogger? Log { get; set; }

    /// <summary>Tells each of <paramref name="listeners"/>, every one whatever the others do.</summary>
    /// <param name="what">What changed, for the log: "the inbox", "a run's questions".</param>
    public static void Tell(Action? listeners, string what)
    {
        if (listeners is null)
            return;
        foreach (var listener in listeners.GetInvocationList())
        {
            try
            {
                ((Action)listener)();
            }
            catch (Exception ex)
            {
                Log?.LogWarning(ex, "A page told that {What} changed failed; the others were still told.", what);
            }
        }
    }
}
