namespace Enactive.App.Ui.ViewModels;

using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>
/// Draws a finished run's step cards from what was recorded, so a run you open from the history
/// reads the way it did while it was happening.
///
/// <para>WHAT the cards are — one per plan step, or the single card of a quick action, or none at
/// all for a record that cannot honestly produce any — is decided by
/// <see cref="RunReplayPlan"/>, in Core, because that is a question about the record. This is only
/// the drawing: it turns each segment into a card and writes its events onto it, using the same
/// vocabulary the live view uses so the two cannot drift apart.</para>
/// </summary>
internal static class RunReplay
{
    /// <summary>The run's cards, or an empty list when the record has nothing to build them from.</summary>
    public static List<StepCardViewModel> Steps(RunRecord record)
    {
        var cards = new List<StepCardViewModel>();

        foreach (var segment in RunReplayPlan.Segments(record))
        {
            var card = new StepCardViewModel(segment.Title);
            cards.Add(card);

            foreach (var e in segment.Events)
                Apply(card, e);

            Finish(card, segment);
        }

        return cards;
    }

    /// <summary>Writes one event onto a card, in the live view's vocabulary.</summary>
    private static void Apply(StepCardViewModel card, RunEventRecord e)
    {
        switch (e.Kind)
        {
            case nameof(EventKind.StepStarted):
                card.SetRunning();
                break;

            case nameof(EventKind.AssistantDelta):
                // Buffered exactly as it is live: the streamed reply is not interesting token by
                // token, and gets folded into one short note when the step moves on.
                card.AppendAssistantText(e.Summary);
                break;

            case nameof(EventKind.ToolInvoked):
                StepCardWriter.LogInvocation(card, e.Summary);
                break;

            case nameof(EventKind.ToolResult):
                card.AppendEntryDetail(e.Summary);
                break;

            case nameof(EventKind.ErrorObserved):
                card.AddNote("⚠ " + e.Summary);
                // Opened, not repainted - the same rule the live feed follows. In a replay the
                // step's final status is already known and would overwrite the colour anyway, so
                // the only thing an amber edge here could do is disagree with it for a moment.
                card.ExpandForAttention();
                break;

            // A refused call is neither a tool that ran nor a remark, and it is told apart by the
            // event's VALUE rather than by matching "denied" in the sentence - three wordings say
            // it. A record from before that value existed has no decision to read and falls back to
            // a note, which is what it always was.
            case nameof(EventKind.DecisionResolved) when WorkEventPayload.WasRefusedIn(e.Payload) == true:
                card.AddRefusal(e.Summary);
                break;

            case nameof(EventKind.ReviewRequested):
            case nameof(EventKind.ReviewPassed):
            case nameof(EventKind.ReviewFailed):
            case nameof(EventKind.DecisionRequested):
            case nameof(EventKind.DecisionResolved):
            case nameof(EventKind.ArtifactReverted):
            case nameof(EventKind.ContextTrimmed):
                card.AddNote(e.Summary);
                break;

            case nameof(EventKind.ArtifactProduced):
                card.AddNote("Artifact: " + e.Summary);
                break;
        }
    }

    /// <summary>
    /// The card's final state. A segment with no outcome never reached an end — the run stopped
    /// inside it — and leaving it "running" forever, or calling it done, are both lies.
    /// </summary>
    private static void Finish(StepCardViewModel card, ReplaySegment segment)
    {
        switch (segment.Outcome)
        {
            case null:
                card.SetFailed();
                card.SetActivity("Never finished — the run ended here");
                return;

            case StepOutcomeKind.Succeeded:
                card.SetDone();
                card.SetActivity("Done");
                return;

            // Skipped is not failed: nothing went wrong in THIS step, and painting it red sends you
            // looking for a fault that is in another card.
            case StepOutcomeKind.Skipped:
                card.SetSkipped();
                card.SetActivity("Skipped — a dependency failed");
                return;

            default:
                card.SetFailed();
                card.SetActivity(segment.Note is { Length: > 0 } note ? note : segment.Outcome.ToString()!);
                card.ExpandForAttention();
                return;
        }
    }
}
