namespace Enactive.App.Ui.ViewModels;

using Enactive.Core.Events;
using Enactive.Workspace;

// What the view models say about what they show, for the view to draw (Palette). Each is the fact a colour, a weight
// or an indent stood for when the view models picked those themselves - kept together, and free of Avalonia, so the
// tests can reach them along with the step cards.

/// <summary>
/// Where a step card stands, as its colour says it: the card's status - except that a card waiting for a person, or
/// blocked until its cause is put right, says so over whatever its status is.
/// </summary>
internal enum CardTone { Pending, Running, Done, Failed, Skipped, Unverified, NeedsYou }

/// <summary>What a line of a unified diff does to the file.</summary>
internal enum DiffLineKind { Added, Removed, Context }

/// <summary>Which model's pill is shown over the run: none yet, the coder working, or the reasoner reviewing.</summary>
internal enum AgentKind { None, Coder, Reasoner }

/// <summary>
/// Where a staged change stands: waiting for a person, applied, rejected - or refused, when applying or rejecting it
/// could not be done (the file changed after the proposal was made) and the person still has to decide.
/// </summary>
internal enum ChangeState { Staged, Applied, Rejected, Refused }

/// <summary>Which of the switcher's rows this is: the one you are in, another one, or one whose folder is gone.</summary>
internal enum WorkspacePlace { Current, Other, Missing }

/// <summary>Whether a schedule will run: off, on but with no time it can ever be run at, or on with a next run.</summary>
internal enum ScheduleState { Off, Untimed, Scheduled }

/// <summary>Where a provider that served a run's tokens is. Unknown is its own answer, never quietly folded into either.</summary>
internal enum ModelReach { Local, Cloud, Unknown }

/// <summary>What a template's edge says: where it comes from, or that it is the one being edited.</summary>
internal enum TemplateEdge { Builtin, Global, Workspace, Selected }

/// <summary>
/// How a line of a run's timeline reads: something went wrong, a review refused the work, the run or a review ended well,
/// the machinery of a call - or none of those, the plain line most of a run is.
/// </summary>
internal enum TimelineTone { Failed, Refused, Succeeded, Quiet, Plain }

internal static class TimelineTones
{
    /// <summary>The kind of a timeline line that is the assistant's reply folded into one note (RunTimeline).</summary>
    public const string AssistantNote = "assistant";

    /// <summary>
    /// The tone of a recorded line, by its kind. A run's record keeps the kind as text; it is read as an
    /// <see cref="EventKind"/> here, where the palette compared the text with names it spelled out - a kind renamed in
    /// Core would have kept its old spelling there and gone plain without a word. A kind this build does not know - one
    /// an older build recorded - is plain, which is what it is.
    /// </summary>
    public static TimelineTone Of(string kind)
        => kind == AssistantNote ? TimelineTone.Quiet
            : Enum.TryParse<EventKind>(kind, out var known) ? Of(known)
            : TimelineTone.Plain;

    private static TimelineTone Of(EventKind kind) => kind switch
    {
        EventKind.ErrorObserved or EventKind.TaskFailed => TimelineTone.Failed,
        EventKind.ReviewFailed => TimelineTone.Refused,
        EventKind.ReviewPassed or EventKind.TaskCompleted => TimelineTone.Succeeded,
        EventKind.ToolInvoked or EventKind.ToolResult => TimelineTone.Quiet,
        _ => TimelineTone.Plain
    };
}

internal static class TemplateEdges
{
    /// <summary>
    /// Where a template comes from, as its edge says - every origin named. One added later is a failure here, as it is
    /// in the palette; it was drawn as a workspace's own without a word.
    /// </summary>
    public static TemplateEdge Of(TemplateOrigin origin) => origin switch
    {
        TemplateOrigin.Builtin => TemplateEdge.Builtin,
        TemplateOrigin.Global => TemplateEdge.Global,
        TemplateOrigin.Workspace => TemplateEdge.Workspace,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "A template's origin with no edge.")
    };
}
