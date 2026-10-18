namespace Enactive.Core.Execution;

/// <summary>
/// A host's request that a run stop after the steps it is running now (Plan Phase 4.2): nothing new is started, what is
/// running finishes and is committed as usual, and the run ends <see cref="Events.RunOutcomeKind.Paused"/> with a
/// checkpoint it is resumed from - exactly the steps that are left, none of them again.
///
/// <para><b>Not Stop.</b> Stop cancels the steps in flight, and a crash resume then does them again from their
/// beginning. A pause waits for them: it ends at a step boundary, so there is nothing to redo.</para>
///
/// <para><b>Whose it is.</b> The host that starts the run makes one, hands it in with the run
/// (<c>RunRequest.Pause</c>, which the composer passes to the engine as <c>RunEngineResources.Pause</c>) and keeps it,
/// and its "Pause after this step" control calls <see cref="Request"/>. One per run: a resumed run is given a new one,
/// or none. A run given none cannot be paused, which is how every run behaved before this existed.</para>
///
/// <para>Thread-safe: the host raises it from its own thread (a button, a request handler, a console signal) while the
/// engine's dispatcher reads it from another.</para>
/// </summary>
public sealed class RunPause
{
    private int _requested;

    /// <summary>
    /// Asks the run to pause once the steps running now have finished. Idempotent, and it cannot be taken back: a
    /// half-withdrawn pause would race the dispatcher deciding what to start next.
    /// </summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);

    /// <summary>Whether a pause has been asked for.</summary>
    public bool IsRequested => Volatile.Read(ref _requested) == 1;
}
