namespace Enactive.Server.Components;

using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.History;
using Enactive.Providers;
using Enactive.Server.Accounts;
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

    /// <summary>
    /// A declared check's verdict on a card, as Palette.CheckVerdict: passed green, failed red, not run amber - it verified
    /// nothing, and must be seen.
    /// </summary>
    public static string Check(CheckVerdict verdict) => verdict switch
    {
        CheckVerdict.Pass => "tone-done",
        CheckVerdict.Fail => "tone-failed",
        CheckVerdict.NotRun => "tone-warning",
        _ => throw Unknown(verdict)
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

    /// <summary>
    /// An inbox item's edge and kind, as Palette.InboxKind: a result green, a question nobody was there to answer amber, an
    /// error red, a paused run amber. The kind is a word kept on disk, not an enum, and - unlike the tables above - one
    /// this does not know is drawn muted rather than refused, as the window draws it: an item filed by a newer build must
    /// still be listed.
    /// </summary>
    public static string Inbox(string kind) => kind.ToLowerInvariant() switch
    {
        "result" => "tone-done",
        "decision" => "tone-open",
        "error" => "tone-failed",
        // Paused by somebody, resumed from where it stopped (Plan Phase 4.2): open, as an unfinished run's pill is.
        "paused" => "tone-open",
        // Lessons a run nobody watched left to decide (Plan Phase 5.2): waiting on a person.
        "lesson" => "tone-open",
        _ => "tone-idle"
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

    /// <summary>
    /// A provider's or an MCP server's edge, as Palette.Local: green when it is on this machine, blue when it is reached
    /// over the network - whether the work leaves the box to reach it.
    /// </summary>
    public static string Local(bool local) => local ? "tone-local" : "tone-cloud";

    /// <summary>
    /// A template's edge in the library, as Palette.Template: grey for a built-in, blue for the global library, green for one
    /// that lives with the project - and amber for the one chosen, whose form is filled in (TemplateListItem.Edge).
    /// </summary>
    public static string Template(TemplateEdge edge) => edge switch
    {
        TemplateEdge.Builtin => "edge-builtin",
        TemplateEdge.Global => "edge-global",
        TemplateEdge.Workspace => "edge-workspace",
        TemplateEdge.Selected => "edge-selected",
        _ => throw Unknown(edge)
    };

    /// <summary>
    /// A schedule's edge in the list, as Palette.Schedule: the colour says what its second line says, for the glance - muted
    /// while it is off, red when it can never be timed, green when it has a next run.
    /// </summary>
    public static string Schedule(ScheduleState state) => state switch
    {
        ScheduleState.Off => "tone-idle",
        ScheduleState.Untimed => "tone-failed",
        ScheduleState.Scheduled => "tone-done",
        _ => throw Unknown(state)
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

    /// <summary>One check's line in a "Run checks now" record: what it came to - a failure red, a check not run amber, a pass plain.</summary>
    public static string CheckLine(CheckLineTone tone) => tone switch
    {
        // Plain, never green: a record is "checked at its time", and a green PASS under it reads as a standing pass.
        CheckLineTone.Passed => "tone-plain",
        CheckLineTone.Failed => "tone-failed",
        CheckLineTone.NotRun => "tone-warning",
        _ => throw Unknown(tone)
    };

    /// <summary>
    /// A "Run checks now" record's edge: amber while the workspace still matches it - checked at its time, never a standing
    /// green - and muted once it is outdated.
    /// </summary>
    public static string CheckRecord(bool outdated) => outdated ? "tone-idle" : "tone-open";

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

    /// <summary>
    /// An account's card: green while it can sign in, amber while a lockout holds it (it frees itself), grey when an
    /// Admin has disabled it - nothing about an account is a failure to draw in red.
    /// </summary>
    public static string Account(AccountStanding standing) => standing switch
    {
        AccountStanding.Active => "tone-done",
        AccountStanding.Locked => "tone-warning",
        AccountStanding.Disabled => "tone-faint",
        _ => throw Unknown(standing)
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        => new(nameof(value), value, $"No look is chosen for {typeof(T).Name} {value}: add it to Looks and theme.css.");
}
