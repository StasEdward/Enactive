namespace Enactive.Server.Runs;

using Enactive.Core.Chat;

/// <summary>
/// The run the code now executing works for, on the service - so a model call's figures reach that run's panel.
///
/// <para><b>Why from the flow.</b> The engine is one for the whole service, and it reports every model call through one
/// callback given when it is built (EngineComposition, <c>metrics</c>), while several runs go at once. A callback that
/// wrote to "the" run would have put one run's calls in another's panel, or in every panel. The engine's executor sets
/// this as a run starts (EngineExecutor.ExecuteAsync); the engine's work runs in that flow - its events are pulled by the
/// run's drain, its pumps are started from there (Task.Run carries the flow), and MeteredChatProvider reports a call in
/// the flow that awaited it - so the callback reads here whose call it was.</para>
/// </summary>
internal static class ServerRunScope
{
    private static readonly AsyncLocal<RunSession?> _current = new();

    /// <summary>The run whose flow this is, or null outside any run's.</summary>
    public static RunSession? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>The engine's callback for a completed model call: into the panel of the run that made it, if any.</summary>
    public static void Report(ModelCallMetrics metrics) => Current?.AddMetrics(metrics);
}
