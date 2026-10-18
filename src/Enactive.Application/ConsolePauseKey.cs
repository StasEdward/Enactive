namespace Enactive.Application;

using Enactive.Application.Projections;

/// <summary>
/// The console's "Pause after this step" (Plan Phase 4.2): the key P, read while a run runs. Ctrl+C stays Stop. A key
/// rather than a second signal: Ctrl+Break is missing from most laptop keyboards and has no counterpart off Windows, and a
/// key the console already reads (its own permission prompt reads lines) needs nothing but a gate around that prompt.
///
/// <para>What a key does and the one line it prints are here, where they are tested without a terminal; the console only
/// reads the key and prints the line. The rule for when a pause is offered is every host's (PauseOffer).</para>
/// </summary>
internal static class ConsolePauseKey
{
    /// <summary>The run banner's line, so the key is known before it is needed.</summary>
    public const string Banner = "P pauses the run after the steps running now (then --resume carries it on); Ctrl+C stops it at once";

    /// <summary>
    /// The line to print for a key pressed during a run, or null for a key that is not P - which does nothing: a run is
    /// not a prompt, and a stray key must not be read as anything.
    /// </summary>
    public static string? Press(char key, PauseOffer offer)
    {
        if (key is not ('p' or 'P'))
            return null;
        var already = offer.Standing == PauseStanding.Pending;
        if (offer.Request() is { } refused)
            return "  >> Not paused: " + refused;
        return already
            ? "  >> The pause is already asked for: the steps running now finish first."
            : "  >> Pausing after the steps running now… Ctrl+C still stops the run at once.";
    }

    /// <summary>
    /// What "--resume" prints of the run it carries on. A run paused at a step boundary is said to be paused: "stopped"
    /// read as something that went wrong, for a run somebody paused on purpose with P.
    /// </summary>
    public static string Resuming(Enactive.Core.History.RunCheckpoint checkpoint)
        => $"Resuming a run {(checkpoint.Paused ? "paused" : "stopped")} on {checkpoint.At.ToLocalTime():yyyy-MM-dd HH:mm}: "
           + $"{checkpoint.Finished} of {checkpoint.Steps.Count} step(s) were done.";
}
