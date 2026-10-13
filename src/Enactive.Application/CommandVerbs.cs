namespace Enactive.Application;

/// <summary>
/// The command bar's verb chips: the verbs offered, and what pressing one does to what was typed. Moved from the window
/// (MainWindowViewModel) so the service's bar offers the same chips and they do the same thing - a second list would
/// drift the first time a verb was added to one bar and not the other.
/// </summary>
public static class CommandVerbs
{
    /// <summary>The chips, in the order the bar shows them.</summary>
    public static readonly IReadOnlyList<string> All = ["Fix", "Build", "Investigate", "Explain", "Refactor"];

    /// <summary>
    /// What was typed with the verb in front: the verb and a space alone when nothing (or only spaces) was typed, so the
    /// person goes on typing after it.
    /// </summary>
    public static string Prefix(string? input, string verb)
        => string.IsNullOrWhiteSpace(input) ? verb + " " : verb + " " + input;
}

/// <summary>
/// What the Run button says over it. Moved from the window (MainWindowViewModel.RunTip) for the service's bar: it wrote
/// the idle line by hand, and once the bar and the run shared a page it said "Run this here" over a button greyed by
/// the run going under it.
/// </summary>
public static class RunButton
{
    /// <summary>
    /// Why the window greys Run: it runs one foreground run at a time, and once a person can walk away from it the reason
    /// stops being obvious - the run holding Run may be in a workspace that is not on screen.
    /// </summary>
    public const string OneForegroundRun =
        "Enactive runs one foreground run at a time, and that run may be in another workspace. Run in background is not held by it.";

    /// <summary>
    /// What Run says, and - when it is greyed (<paramref name="busy"/>) - why, in the host's own words
    /// (<paramref name="heldBecause"/>): what holds Run is the host's. The window's reason was every host's, and on the
    /// service, whose bar is held only by the run open under it while other runs wait in the service's line, it was untrue.
    /// </summary>
    public static string Tip(bool busy, string heldBecause = OneForegroundRun)
        => busy
            ? "A run is already in progress. " + heldBecause
            : "Run this here, and watch it.";
}
