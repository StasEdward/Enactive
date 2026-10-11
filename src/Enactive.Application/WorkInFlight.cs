namespace Enactive.Application;

/// <summary>
/// What a host would cut short by stopping now, and how to say so to the person stopping it.
///
/// <para>Counted by the host - which runs it owns is its own business - and said here, so the window's "close?" and
/// the service's planned stop say the same thing about the same work.</para>
/// </summary>
/// <param name="Foreground">A run somebody is watching is going.</param>
/// <param name="DecisionPending">A run is stopped at a question on screen.</param>
/// <param name="Background">Runs nobody is watching.</param>
/// <param name="Remote">
/// Runs a phone started. They die with the process exactly as a background run does, and the person being asked about
/// stopping is not the person watching them - which is the reason to say so rather than count them in silently.
/// </param>
internal readonly record struct WorkInFlight(bool Foreground, bool DecisionPending, int Background, int Remote)
{
    /// <summary>Work that must be cancelled and drained before the process exits.</summary>
    public bool Any => Foreground || DecisionPending || Background > 0 || Remote > 0;

    public string Describe()
    {
        if (DecisionPending)
            return "A run is waiting for your decision. Closing now cancels it.";

        if (Remote > 0)
            return Remote == 1
                ? "A task started from your phone is still going. Closing now stops it where it is."
                : $"{Remote} tasks started from your phone are still going. Closing now stops them where they are.";
        if (Foreground && Background > 0)
            return $"A run is going, and {Background} more in the background. Closing cancels them and waits for cleanup and recording.";
        if (Foreground)
            return "A run is going. Closing cancels it and waits for cleanup and recording.";
        return Background == 1
            ? "A background run is still going. Closing cancels it and waits for cleanup and recording."
            : $"{Background} background runs are still going. Closing cancels them and waits for cleanup and recording.";
    }
}
