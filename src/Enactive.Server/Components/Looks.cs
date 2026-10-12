namespace Enactive.Server.Components;

using Enactive.Application.Projections;
using Enactive.Core.History;
using Enactive.Providers;
using Enactive.Workspace;

/// <summary>
/// What colour a fact is drawn in on the web: the CSS class (wwwroot/theme.css) for each state, value for value what
/// the window's Palette.cs gives it. A page says what is the case and asks for the class; it never picks a colour.
///
/// <para>Each table over an enum names every value and throws on one it does not know, rather than falling back: a new
/// state that quietly drew in the fallback colour is the state nobody noticed was new. LooksTests asks every value of
/// every one, and holds the colour behind each class to the one Palette.cs gives the same state.</para>
/// </summary>
internal static class Looks
{
    /// <summary>A step card's edge. The window's Pending is faint, Blocked is the card that waits for a person (amber).</summary>
    public static string Card(FeedCardStatus status) => status switch
    {
        FeedCardStatus.Pending => "tone-faint",
        FeedCardStatus.Running => "tone-running",
        FeedCardStatus.Done => "tone-done",
        FeedCardStatus.Failed => "tone-failed",
        FeedCardStatus.Skipped => "tone-skipped",
        FeedCardStatus.Unverified => "tone-unverified",
        FeedCardStatus.Blocked => "tone-warning",
        _ => throw Unknown(status)
    };

    /// <summary>
    /// A run's edge in a list, as Palette.RunEdge: done green, failed red, idle muted - and amber for every other word,
    /// a run in progress included, because the window draws a status it has no end for as a person's problem to look at.
    /// </summary>
    public static string Run(RunStandingKind standing) => standing switch
    {
        RunStandingKind.Done => "tone-done",
        RunStandingKind.Failed => "tone-failed",
        RunStandingKind.Idle => "tone-idle",
        RunStandingKind.Running or RunStandingKind.Open => "tone-open",
        _ => throw Unknown(standing)
    };

    /// <summary>
    /// The live run's phase pill, as Brand.PhaseFill and PhaseText: done green, failed red, waiting on a person amber, idle
    /// muted - and a run going blue, where the history's edge (<see cref="Run"/>) draws it amber. One table for both looked
    /// like a run in trouble on the pill of every run in progress.
    /// </summary>
    public static string Phase(RunStandingKind standing) => standing switch
    {
        RunStandingKind.Done => "tone-done",
        RunStandingKind.Failed => "tone-failed",
        RunStandingKind.Open => "tone-open",
        RunStandingKind.Idle => "tone-idle",
        RunStandingKind.Running => "tone-running",
        _ => throw Unknown(standing)
    };

    /// <summary>A line of a diff, as Palette.DiffLine: added green, removed red, the rest muted.</summary>
    public static string Diff(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => "tone-done",
        DiffLineKind.Removed => "tone-failed",
        DiffLineKind.Context => "tone-idle",
        _ => throw Unknown(kind)
    };

    /// <summary>A log line by its level, as Palette.LogLevel: the quieter the level, the fainter the line.</summary>
    public static string Log(Core.Diagnostics.LogLevel level) => level switch
    {
        Core.Diagnostics.LogLevel.Trace => "tone-faint",
        Core.Diagnostics.LogLevel.Debug => "tone-idle",
        Core.Diagnostics.LogLevel.Info => "tone-bright",
        Core.Diagnostics.LogLevel.Warn => "tone-warning",
        Core.Diagnostics.LogLevel.Error => "tone-failed",
        _ => throw Unknown(level)
    };

    /// <summary>The icon of a line on a step card, as Palette.EntryIcon: what the step did, by its kind.</summary>
    public static string Entry(FeedEntryKind kind) => kind switch
    {
        FeedEntryKind.Command or FeedEntryKind.Tool => "tone-running",
        FeedEntryKind.File => "tone-done",
        FeedEntryKind.Note => "tone-warning",
        FeedEntryKind.Refusal => "tone-failed",
        _ => throw Unknown(kind)
    };

    /// <summary>
    /// A step card's edge, as StepCardViewModel.ToneOf: a question waiting for a person outranks the status - the step has
    /// not failed, it waits - and is drawn as a blocked one is.
    /// </summary>
    public static string Card(FeedCardStatus status, bool waitingForYou)
        => waitingForYou ? Card(FeedCardStatus.Blocked) : Card(status);

    /// <summary>
    /// A workspace's edge in the switcher and on the WORKSPACE card, as Palette.WorkspaceEdge: every row has one, so the
    /// column is one to scan for "which of these is gone" - blue where you are, red where the folder is gone, grey else.
    /// </summary>
    public static string Workspace(WorkspacePlace place) => place switch
    {
        WorkspacePlace.Current => "edge-current",
        WorkspacePlace.Missing => "edge-missing",
        WorkspacePlace.Other => "edge-other",
        _ => throw Unknown(place)
    };

    /// <summary>A line of a run's timeline, by the kind of event it was.</summary>
    public static string Timeline(TimelineTone tone) => tone switch
    {
        TimelineTone.Failed => "tone-failed",
        TimelineTone.Refused => "tone-warning",
        TimelineTone.Succeeded => "tone-done",
        TimelineTone.Quiet => "tone-idle",
        TimelineTone.Plain => "tone-plain",
        _ => throw Unknown(tone)
    };

    /// <summary>Green when it ran on this machine, blue when it did not; faint where nobody knows.</summary>
    public static string Reach(ModelReach reach) => reach switch
    {
        ModelReach.Local => "tone-local",
        ModelReach.Cloud => "tone-cloud",
        ModelReach.Unknown => "tone-faint",
        _ => throw Unknown(reach)
    };

    /// <summary>A staged change: amber while it waits for a person or could not be applied, then what was done.</summary>
    public static string Change(bool applied, bool rejected) => (applied, rejected) switch
    {
        (false, false) => "tone-open",
        (true, false) => "tone-done",
        (false, true) => "tone-failed",
        // Both at once is not a state a change is in; the window's ChangeState cannot say it either.
        _ => throw Unknown((applied, rejected))
    };

    /// <summary>A tier, 0 Observe to 3 Autonomous: a risk scale, green through blue and yellow to red.</summary>
    public static string Autonomy(int level) => level switch
    {
        >= 0 and <= 3 => $"tier-{level}",
        _ => throw Unknown(level)
    };

    /// <summary>A pull request's checks: passed, failed, still running - or none reported.</summary>
    public static string Ci(CiState state) => state switch
    {
        CiState.Passing => "tone-done",
        CiState.Failing => "tone-failed",
        CiState.Pending => "tone-warning",
        CiState.None => "tone-faint",
        _ => throw Unknown(state)
    };

    /// <summary>
    /// Three answers, three colours, and grey for "nobody has asked" - never green. Amber rather than red for a missing
    /// model: the provider answered and the credential was accepted, so nothing is broken; something is not installed
    /// or is misspelled, and that is a different repair.
    /// </summary>
    public static string Health(ProviderHealth health) => health switch
    {
        ProviderHealth.Ready => "tone-done",
        ProviderHealth.ModelMissing => "tone-warning",
        ProviderHealth.Unreachable => "tone-failed",
        ProviderHealth.Unknown or ProviderHealth.Checking => "tone-faint",
        _ => throw Unknown(health)
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        => new(nameof(value), value, $"No look is chosen for {typeof(T).Name} {value}: add it to Looks and theme.css.");
}
