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
