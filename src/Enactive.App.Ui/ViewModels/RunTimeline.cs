namespace Enactive.App.Ui.ViewModels;

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
            rows.Add(new RunEventViewModel(bufferAt, "assistant", text));
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
