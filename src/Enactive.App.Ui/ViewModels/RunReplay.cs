namespace Enactive.App.Ui.ViewModels;

using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>
/// Rebuilds a finished run's step cards from what was recorded, so a run you open from the history
/// reads the way it did while it was happening.
///
/// It attributes an event to a step ONLY by the step number stored on the event. Nothing is guessed
/// from the order: with more than one step in flight, "the step that started most recently" is not
/// the step a tool call came from, and this app has already shipped that bug once. An event with no
/// step number belongs to the run rather than to a card, and stays in the timeline.
/// </summary>
internal static class RunReplay
{
    /// <summary>
    /// The run's steps, or an empty list when the record has nothing to build them from - a run
    /// stored before step numbers were recorded, or one that never got a plan.
    /// </summary>
    public static List<StepCardViewModel> Steps(RunRecord record)
    {
        var cards = TitlesFromPlan(record);
        if (cards.Count == 0)
            return cards;

        var touched = false;

        foreach (var e in record.Events)
        {
            if (e.Step is not { } step || step - 1 < 0 || step - 1 >= cards.Count)
                continue;

            var card = cards[step - 1];
            touched = true;

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
                    card.ExpandForAttention();
                    break;

                case nameof(EventKind.ReviewRequested):
                case nameof(EventKind.ReviewPassed):
                case nameof(EventKind.ReviewFailed):
                case nameof(EventKind.DecisionRequested):
                case nameof(EventKind.DecisionResolved):
                    card.AddNote(e.Summary);
                    break;

                case nameof(EventKind.ArtifactProduced):
                    card.AddNote("Artifact: " + e.Summary);
                    break;

                case nameof(EventKind.StepCompleted):
                    Finish(card, e.Summary);
                    break;
            }
        }

        // Cards with a plan but no attributed events are a record from before step numbers existed.
        // Showing them as a column of "pending" steps would be a fiction; the timeline is the truth.
        if (!touched)
            return new List<StepCardViewModel>();

        // A run that died mid-step leaves its card running forever otherwise.
        foreach (var card in cards)
            if (card.StatusWord == "running")
            {
                card.SetFailed();
                card.SetActivity("Never finished — the run ended here");
            }

        return cards;
    }

    /// <summary>Reads the step titles out of "&lt;title&gt; — N steps: a | b", the same string the
    /// live view builds its cards from.</summary>
    private static List<StepCardViewModel> TitlesFromPlan(RunRecord record)
    {
        var cards = new List<StepCardViewModel>();

        var plan = record.Events.FirstOrDefault(e => e.Kind == nameof(EventKind.PlanCreated));
        if (plan is null)
            return cards;

        const string marker = " steps: ";
        var index = plan.Summary.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return cards;

        foreach (var title in plan.Summary[(index + marker.Length)..]
                     .Split(" | ", StringSplitOptions.RemoveEmptyEntries))
            cards.Add(new StepCardViewModel(title.Trim()));

        return cards;
    }

    private static void Finish(StepCardViewModel card, string summary)
    {
        // The same three outcomes the live view reads out of one event kind.
        if (summary.Contains("skipped (dependency failed)", StringComparison.Ordinal))
        {
            card.SetSkipped();
            card.SetActivity("Skipped — a dependency failed");
            return;
        }

        if (summary.Contains("FAILED:", StringComparison.Ordinal))
        {
            card.SetFailed();
            card.SetActivity("Failed");
            card.ExpandForAttention();
            return;
        }

        card.SetDone();
        card.SetActivity("Done");
    }
}
