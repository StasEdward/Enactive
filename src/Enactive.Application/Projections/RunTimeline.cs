namespace Enactive.Application.Projections;

using System.Text;
using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>
/// Turns a stored run into lines a person can read.
///
/// One rule, and it lives here so nobody has to remember it twice: the assistant's reply is recorded
/// one event PER TOKEN, so replaying a run's events verbatim buries everything that actually happened
/// under a column of single words. A run of deltas is folded back into one short note. Every view
/// that replays <see cref="RunRecord.Events"/> goes through this.
/// </summary>
internal static class RunTimeline
{
    /// <summary>A folded assistant note past this length is cut - the point is the shape of the run.</summary>
    private const int MaxAssistantText = 300;

    public static List<RunEventViewModel> Fold(RunRecord run)
    {
        var rows = new List<RunEventViewModel>();
        var buffer = new StringBuilder();
        var bufferAt = default(DateTimeOffset);

        void FlushAssistant()
        {
            if (buffer.Length == 0)
                return;
            var text = buffer.ToString().Replace('\n', ' ').Replace('\r', ' ').Trim();
            buffer.Clear();
            if (text.Length == 0)
                return;
            if (text.Length > MaxAssistantText)
                text = text[..MaxAssistantText] + "…";
            rows.Add(new RunEventViewModel(bufferAt, TimelineTones.AssistantNote, text));
        }

        foreach (var e in run.Events)
        {
            if (e.Kind == nameof(EventKind.AssistantDelta))
            {
                if (buffer.Length == 0)
                    bufferAt = e.At;
                buffer.Append(e.Summary);
                continue;
            }

            FlushAssistant();
            rows.Add(new RunEventViewModel(e.At, e.Kind, e.Summary));
        }

        FlushAssistant();
        return rows;
    }
}

/// <summary>One line of the run's timeline.</summary>
internal sealed class RunEventViewModel
{
    public RunEventViewModel(DateTimeOffset at, string kind, string summary)
    {
        When = Times.Seconds(at);
        Kind = kind;
        Summary = summary;
        Tone = TimelineTones.Of(kind);
    }

    public string When { get; }
    public string Kind { get; }
    public string Summary { get; }

    /// <summary>How the line reads - what its colour says (Palette.Timeline).</summary>
    public TimelineTone Tone { get; }
}

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
