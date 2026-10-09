using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.Diagnostics;
using Enactive.Core.History;
using Enactive.Providers;

namespace Enactive.App.Ui;

/// <summary>
/// What colour - or weight, or indent - a fact is drawn in: the view's half of every view model that shows a
/// state. A view model says what is the case (a card's tone, a run's status, a log line's level); a view binds
/// that through one of these and gets the brush.
///
/// <para><b>Why here and not in the view models.</b> Until 2026-10-08 fourteen view models held brushes, and the
/// same table was written two and three times: a run's status to a colour in the runs list and again in Brand's
/// pill helpers, an Inbox item's kind in the Inbox and again in the schedules window. Holding Avalonia's types also
/// kept those view models out of the tests, which build only the window's Avalonia-free files - the step cards had
/// none. A rule only some of them followed would have been two rules; ViewModelsHoldNoLooksTests keeps it one.</para>
///
/// <para>Each table that is over an enum names every value and throws on one it does not know, rather than
/// falling back: a new state that quietly drew in the fallback colour is the state nobody noticed was new.
/// ViewModelsHoldNoLooksTests asks every value of every one.</para>
/// </summary>
internal static class Palette
{
    // ── a run's step cards ──────────────────────────────────────────────────

    public static readonly IValueConverter CardTone = Map<CardTone>(tone => tone switch
    {
        ViewModels.CardTone.Pending => Brand.StepPending,
        ViewModels.CardTone.Running => Brand.StepRunning,
        ViewModels.CardTone.Done => Brand.StepDone,
        ViewModels.CardTone.Failed => Brand.StepFailed,
        ViewModels.CardTone.Skipped => Brand.StepSkipped,
        ViewModels.CardTone.Unverified => Brand.StepUnverified,
        // Amber: nothing went wrong in it - it waits for a person, or for its cause to be put right.
        ViewModels.CardTone.NeedsYou => Brand.Warning,
        _ => throw Unknown(tone)
    });

    public static readonly IValueConverter EntryIcon = Map<FeedEntryKind>(kind => kind switch
    {
        FeedEntryKind.Command => Brand.Info,
        FeedEntryKind.File => Brand.Success,
        FeedEntryKind.Note => Brand.Warning,
        FeedEntryKind.Refusal => Brand.Danger,
        FeedEntryKind.Tool => Brand.Info,
        _ => throw Unknown(kind)
    });

    /// <summary>What the model said stands out among what it did; the rest is the plain record of actions.</summary>
    public static readonly IValueConverter EntryWeight = new FuncValueConverter<FeedEntryKind, FontWeight>(
        kind => kind == FeedEntryKind.Note ? FontWeight.SemiBold : FontWeight.Normal);

    // ── a run's status, wherever it is shown ────────────────────────────────
    //
    // Which kind of ending a stored status is, is decided once, in Core (RunStanding), where it is tested
    // against every outcome the engine records. Only the colours are here.

    /// <summary>The edge of a run in the history.</summary>
    public static readonly IValueConverter RunEdge = Map<string>(status => RunStanding.Of(status) switch
    {
        RunStandingKind.Done => Brand.Success,
        RunStandingKind.Failed => Brand.Danger,
        RunStandingKind.Idle => Brand.TextMuted,
        // Open - blocked, waiting for an answer, incomplete - and any word that is not an end: a run that
        // never wrote a final status. A person's problem to look at, which is what amber means here.
        _ => Brand.Amber
    });

    /// <summary>A phase pill's tint.</summary>
    public static readonly IValueConverter PhaseFill = Map<string>(Brand.PhaseFill);

    /// <summary>A phase pill's word: the base colour is too dark on its own tint, so running takes the light blue.</summary>
    public static readonly IValueConverter PhaseText = Map<string>(Brand.PhaseText);

    // ── how much may be done without asking ─────────────────────────────────

    /// <summary>A tier, 0 Observe … 3 Autonomous; muted where none is known (a run recorded before tiers were).</summary>
    public static readonly IValueConverter Autonomy = new FuncValueConverter<int?, IBrush>(
        level => level is { } known ? Brand.Autonomy(known) : Brand.TextMuted);

    // ── the window around a run ─────────────────────────────────────────────

    public static readonly IValueConverter Agent = Map<AgentKind>(kind => kind switch
    {
        AgentKind.None => Brand.Line,
        AgentKind.Coder => Brand.PillCoder,
        AgentKind.Reasoner => Brand.PillReasoner,
        _ => throw Unknown(kind)
    });

    /// <summary>A staged change: amber while it waits for a person or could not be applied, then what was done.</summary>
    public static readonly IValueConverter Change = Map<ChangeState>(state => state switch
    {
        ChangeState.Staged or ChangeState.Refused => Brand.Amber,
        ChangeState.Applied => Brand.Success,
        ChangeState.Rejected => Brand.Danger,
        _ => throw Unknown(state)
    });

    public static readonly IValueConverter DiffLine = Map<DiffLineKind>(kind => kind switch
    {
        DiffLineKind.Added => Brand.Success,
        DiffLineKind.Removed => Brand.Danger,
        DiffLineKind.Context => Brand.TextMuted,
        _ => throw Unknown(kind)
    });

    /// <summary>The open workspace's name: red when its folder is gone, so the run refusing to start is no surprise.</summary>
    public static readonly IValueConverter MissingName = Map<bool>(missing => missing ? Brand.Danger : Brand.Text);

    /// <summary>The open workspace's edge - the same as its row in the switcher, so the two read as one object.</summary>
    public static readonly IValueConverter MissingEdge = Map<bool>(missing => missing ? Brand.Danger : Brand.Current);

    // ── the workspace switcher ──────────────────────────────────────────────

    /// <summary>
    /// EVERY row gets an edge, not just the current one: an edge on one row reads as decoration, an edge on all
    /// of them is a column to scan for "which of these is gone". A missing folder outranks being current.
    /// </summary>
    public static readonly IValueConverter WorkspaceEdge = Map<WorkspacePlace>(place => place switch
    {
        WorkspacePlace.Missing => Brand.Danger,
        WorkspacePlace.Current => Brand.Current,
        WorkspacePlace.Other => Brand.LineStrong,
        _ => throw Unknown(place)
    });

    /// <summary>The row you are in sits a little brighter: the edge carries the meaning, the fill only says where you are.</summary>
    public static readonly IValueConverter WorkspaceFill = Map<bool>(current => current ? Brand.CardFillActive : Brand.CardFill);

    public static readonly IValueConverter WorkspaceName = Map<bool>(exists => exists ? Brand.Text : Brand.TextMuted);

    public static readonly IValueConverter WorkspaceDetail = Map<bool>(exists => exists ? Brand.TextMuted : Brand.Danger);

    // ── lists of things that are here or elsewhere ──────────────────────────

    /// <summary>
    /// Green when it is on this machine, blue when it is not: the one thing about a provider or a tool server
    /// worth seeing without reading - whether your work leaves the box to reach it.
    /// </summary>
    public static readonly IValueConverter Local = Map<bool>(local => local ? Brand.Success : Brand.Info);

    /// <summary>The same green and blue for which side of the box served a run's tokens; faint where nobody knows.</summary>
    public static readonly IValueConverter Reach = Map<ModelReach>(reach => reach switch
    {
        ModelReach.Local => Brand.Success,
        ModelReach.Cloud => Brand.Info,
        ModelReach.Unknown => Brand.TextFaint,
        _ => throw Unknown(reach)
    });

    /// <summary>
    /// Three answers, three colours, and grey for "nobody has asked" - never green. Amber rather than red for a
    /// missing model: the provider answered and the credential was accepted, so nothing is broken; something is
    /// not installed or is misspelled, and that is a different repair.
    /// </summary>
    /// <summary>A pull request's checks: passed, failed, still running - or none reported.</summary>
    public static readonly IValueConverter Ci = Map<Enactive.Workspace.CiState>(state => state switch
    {
        Enactive.Workspace.CiState.Passing => Brand.Success,
        Enactive.Workspace.CiState.Failing => Brand.Danger,
        Enactive.Workspace.CiState.Pending => Brand.Warning,
        Enactive.Workspace.CiState.None => Brand.TextFaint,
        _ => throw Unknown(state)
    });

    public static readonly IValueConverter Health = Map<ProviderHealth>(health => health switch
    {
        ProviderHealth.Ready => Brand.Success,
        ProviderHealth.ModelMissing => Brand.Warning,
        ProviderHealth.Unreachable => Brand.Danger,
        ProviderHealth.Unknown or ProviderHealth.Checking => Brand.TextFaint,
        _ => throw Unknown(health)
    });

    /// <summary>
    /// Grey for a built-in, blue for one of yours, green for one that lives with the project - the distinction
    /// the provider and MCP lists draw, learned once. Selection wins over place on the selected row: where a
    /// template comes from is still a word underneath, "the one being edited" is only ever the highlight.
    /// </summary>
    public static readonly IValueConverter Template = Map<TemplateEdge>(edge => edge switch
    {
        TemplateEdge.Builtin => Brand.Line,
        TemplateEdge.Global => Brand.Info,
        TemplateEdge.Workspace => Brand.Success,
        TemplateEdge.Selected => Brand.Amber,
        _ => throw Unknown(edge)
    });

    /// <summary>Muted for a schedule that is off, red for one that can never be timed, green for one with a next run.</summary>
    public static readonly IValueConverter Schedule = Map<ScheduleState>(state => state switch
    {
        ScheduleState.Off => Brand.TextMuted,
        ScheduleState.Untimed => Brand.Danger,
        ScheduleState.Scheduled => Brand.Success,
        _ => throw Unknown(state)
    });

    // ── what came back while nobody was watching ────────────────────────────

    /// <summary>An Inbox item's kind - a result, a question nobody was there to answer, an error - in the Inbox and in a schedule's past.</summary>
    public static readonly IValueConverter InboxKind = Map<string>(kind => kind.ToLowerInvariant() switch
    {
        "result" => Brand.Success,
        "decision" => Brand.Amber,
        "error" => Brand.Danger,
        _ => Brand.TextMuted
    });

    /// <summary>A line of a run's timeline, by the kind of event it was.</summary>
    public static readonly IValueConverter Timeline = Map<TimelineTone>(tone => tone switch
    {
        TimelineTone.Failed => Brand.Danger,
        TimelineTone.Refused => Brand.Warning,
        TimelineTone.Succeeded => Brand.Success,
        TimelineTone.Quiet => Brand.TextMuted,
        TimelineTone.Plain => Brand.TextBody,
        _ => throw Unknown(tone)
    });

    /// <summary>
    /// A log line by its level. One brush per level, shared by every line - as the log rows kept them - because a
    /// log holds tens of thousands of lines.
    /// </summary>
    private static readonly IBrush[] LogBrushes = Enum.GetValues<Core.Diagnostics.LogLevel>()
        .Select(level => (IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(level switch
        {
            Core.Diagnostics.LogLevel.Trace => Brand.Ink400,
            Core.Diagnostics.LogLevel.Debug => Brand.Ink300,
            Core.Diagnostics.LogLevel.Info => Brand.Ink100,
            Core.Diagnostics.LogLevel.Warn => Brand.WarningColor,
            Core.Diagnostics.LogLevel.Error => Brand.DangerColor,
            _ => throw Unknown(level)
        })).ToArray();

    public static readonly IValueConverter LogLevel = Map<Core.Diagnostics.LogLevel>(level => LogBrushes[(int)level]);

    // ── weight and place ────────────────────────────────────────────────────

    /// <summary>Unread carries its weight, the way an unread mail does.</summary>
    public static readonly IValueConverter Bold = new FuncValueConverter<bool, FontWeight>(
        bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>
    /// A run's row in the history, indented when it is an older attempt so it reads as belonging to the row
    /// above. The WHOLE margin, not the indent alone: a bound Margin replaces the one Border.card sets in the
    /// style, gap included, and an indent with nothing else once took the 6px between every row away.
    /// </summary>
    public static readonly IValueConverter AttemptMargin = new FuncValueConverter<bool, Thickness>(
        lead => lead ? new Thickness(0, 0, 0, 6) : new Thickness(14, 0, 0, 6));

    private static IValueConverter Map<T>(Func<T, IBrush> pick) => new FuncValueConverter<T, IBrush>(value => pick(value!));

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        => new(nameof(value), value, $"No colour is chosen for {typeof(T).Name}.{value}: add it to Palette.");

}
