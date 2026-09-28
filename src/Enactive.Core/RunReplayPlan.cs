namespace Enactive.Core.History;

using Enactive.Core.Events;

/// <summary>
/// One card's worth of a replayed run: what to call it, which of the run's events belong to it, and
/// how it ended.
/// </summary>
/// <param name="Outcome">
/// Null means the segment never reached an end — the run stopped in the middle of it. That is not
/// success and must not be drawn as one.
/// </param>
public sealed record ReplaySegment(
    string Title,
    int? StepNumber,
    IReadOnlyList<RunEventRecord> Events,
    StepOutcomeKind? Outcome,
    string? Note = null);

/// <summary>
/// Deciding what a finished run's Execution view is made of, from the record alone.
///
/// <para>This lives here, away from the UI, because it is a question about the RECORD and it is the
/// part that was wrong. Replay attributed events to steps by the step number stored on each event —
/// correct, and nothing is ever guessed from order, because with several steps in flight "the step
/// that started most recently" is not the step a tool call came from. But it then assumed every run
/// had steps. A QUICK ACTION has no plan and no steps, so none of its events carries a number, and
/// the rebuild produced nothing: an empty Execution tab under "recorded before step numbers were".
/// The record was current; the run simply never had steps. The planner is told to strongly prefer
/// quick actions, so that was most runs — the cards were there while it ran and gone as soon as you
/// looked at anything else.</para>
/// </summary>
public static class RunReplayPlan
{
    private const string QuickActionMarker = "Quick action: ";
    private const string StepsMarker = " steps: ";

    /// <summary>
    /// The segments to draw, in order, or an empty list when the record cannot honestly produce any:
    /// a planned run whose events carry no step numbers (recorded before they existed — there is no
    /// way to say which card a tool call belonged to), or a run that did nothing worth showing.
    /// </summary>
    public static IReadOnlyList<ReplaySegment> Segments(RunRecord record)
    {
        var titles = PlanTitles(record);
        return titles.Count > 0 ? PlannedSegments(record, titles) : QuickActionSegment(record);
    }

    /// <summary>Whether an event is something a card would show. Bookkeeping is not work.</summary>
    public static bool IsVisibleWork(string kind)
        => kind is nameof(EventKind.StepStarted)
                or nameof(EventKind.AssistantDelta)
                or nameof(EventKind.ToolInvoked)
                or nameof(EventKind.ToolResult)
                or nameof(EventKind.ErrorObserved)
                or nameof(EventKind.ReviewRequested)
                or nameof(EventKind.ReviewPassed)
                or nameof(EventKind.ReviewFailed)
                or nameof(EventKind.DecisionRequested)
                or nameof(EventKind.DecisionResolved)
                or nameof(EventKind.ArtifactProduced)
                or nameof(EventKind.ArtifactReverted)
                or nameof(EventKind.ContextTrimmed);

    private static IReadOnlyList<ReplaySegment> PlannedSegments(RunRecord record, List<string> titles)
    {
        var buckets = new List<RunEventRecord>[titles.Count];
        var outcomes = new StepOutcomeKind?[titles.Count];
        var notes = new string?[titles.Count];
        var attributed = false;

        for (var i = 0; i < titles.Count; i++)
            buckets[i] = new List<RunEventRecord>();

        foreach (var e in record.Events)
        {
            if (e.Step is not { } step || step - 1 < 0 || step - 1 >= titles.Count)
                continue;

            attributed = true;

            if (e.Kind == nameof(EventKind.StepCompleted))
            {
                outcomes[step - 1] = StepOutcomeOf(e);
                notes[step - 1] = StepReasonOf(e);
            }
            else
            {
                buckets[step - 1].Add(e);
            }
        }

        // A plan whose events carry no step numbers is a record from before they existed. Drawing a
        // column of empty steps would be a fiction; the timeline is what that record actually holds.
        if (!attributed)
            return Array.Empty<ReplaySegment>();

        var segments = new List<ReplaySegment>(titles.Count);
        for (var i = 0; i < titles.Count; i++)
            segments.Add(new ReplaySegment(titles[i], i + 1, buckets[i], outcomes[i], notes[i]));
        return segments;
    }

    /// <summary>
    /// Why a step ended the way it did — the typed <c>reason</c>, and for a record written before it
    /// existed, the tail of the summary after the outcome word.
    ///
    /// <para>Nothing for a step that succeeded: "why did this work" is not a question, and a line
    /// under every green card is a line nobody reads.</para>
    ///
    /// <para>The fallback parses "[1/2] Title — INCOMPLETE: &lt;reason&gt;", which is the shape the
    /// orchestrator wrote and is exactly as fragile as it looks. It is here so an old run says
    /// something rather than nothing, and it is never consulted for a run recorded since.</para>
    /// </summary>
    private static string? StepReasonOf(RunEventRecord e)
    {
        var outcome = StepOutcomeOf(e);
        if (outcome is StepOutcomeKind.Succeeded or StepOutcomeKind.Skipped)
            return null;

        var reason = Stand(e).OutcomeReason() is { Length: > 0 } typed ? typed : FromSummary(e.Summary);

        // The outcome word stays in front of it. "unresolved tool call: git …" alone does not say
        // whether the step failed or merely did not finish, and those are different cards.
        return reason is null ? null : $"{outcome} — {reason}";
    }

    /// <summary>The tail of "[1/2] Title — INCOMPLETE: &lt;reason&gt;", for records written before
    /// the reason was a value.</summary>
    private static string? FromSummary(string summary)
    {
        var colon = summary.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0 || colon + 2 >= summary.Length)
            return null;

        return summary[(colon + 2)..].Trim() is { Length: > 0 } tail ? tail : null;
    }

    private static IReadOnlyList<ReplaySegment> QuickActionSegment(RunRecord record)
    {
        var events = new List<RunEventRecord>();
        RunEventRecord? terminal = null;

        foreach (var e in record.Events)
            if (e.Kind is nameof(EventKind.TaskCompleted) or nameof(EventKind.TaskFailed))
                terminal = e;
            else
                events.Add(e);

        if (!events.Any(e => IsVisibleWork(e.Kind)))
            return Array.Empty<ReplaySegment>();

        var (outcome, note) = TerminalOutcome(terminal);
        return new[] { new ReplaySegment(QuickActionTitle(record), null, events, outcome, note) };
    }

    /// <summary>How a quick action ended, read from the terminal event's typed outcome.</summary>
    private static (StepOutcomeKind? Outcome, string? Note) TerminalOutcome(RunEventRecord? terminal)
    {
        if (terminal is null)
            return (null, null);

        var stand = Stand(terminal);
        var kind = stand.Outcome()
                   ?? (terminal.Kind == nameof(EventKind.TaskCompleted)
                       ? RunOutcomeKind.Completed
                       : RunOutcomeKind.Failed);

        var outcome = kind switch
        {
            RunOutcomeKind.Completed => StepOutcomeKind.Succeeded,
            RunOutcomeKind.Incomplete or RunOutcomeKind.NeedsUser => StepOutcomeKind.Incomplete,
            _ => StepOutcomeKind.Failed
        };

        var reason = stand.OutcomeReason();
        var note = kind == RunOutcomeKind.Completed
            ? null
            : string.IsNullOrWhiteSpace(reason) ? kind.ToString() : $"{kind} — {reason}";

        return (outcome, note);
    }

    /// <summary>
    /// How a step ended. The typed payload first; the string search is the fallback for a run an
    /// earlier build recorded, and is exactly the fragility it replaced — rewording a summary used
    /// to turn a red card green.
    /// </summary>
    private static StepOutcomeKind StepOutcomeOf(RunEventRecord e)
    {
        if (Stand(e).StepOutcome() is { } typed)
            return typed;

        if (e.Summary.Contains("skipped (dependency failed)", StringComparison.Ordinal))
            return StepOutcomeKind.Skipped;

        return e.Summary.Contains("FAILED:", StringComparison.Ordinal)
            ? StepOutcomeKind.Failed
            : StepOutcomeKind.Succeeded;
    }

    /// <summary>Only the payload matters to the readers, so a stand-in carrying it reads a stored
    /// row through exactly the same code as a live event.</summary>
    private static WorkEvent Stand(RunEventRecord e)
        => new(Guid.Empty, Guid.Empty, Guid.Empty, e.At, EventKind.TaskCompleted, e.Summary, e.Payload);

    /// <summary>
    /// The step titles. From the event's payload, where they are values; the sentence is read only
    /// for a run recorded before the payload existed. Splitting "&lt;title&gt; — N steps: a | b" made
    /// a step whose own title contains " | " into two cards.
    /// </summary>
    private static List<string> PlanTitles(RunRecord record)
    {
        var titles = new List<string>();

        var plan = record.Events.FirstOrDefault(e => e.Kind == nameof(EventKind.PlanCreated));
        if (plan is null)
            return titles;

        if (Stand(plan).PlanSteps() is { Count: > 0 } typed)
        {
            titles.AddRange(typed);
            // And the steps the plan grew while it ran (Phase 5.3), numbered after the ones it had -
            // without them every step for an item was a number past the end, and replay dropped it.
            foreach (var grown in record.Events.Where(e => e.Kind == nameof(EventKind.PlanExpanded)))
                if (Stand(grown).PlanSteps() is { Count: > 0 } added)
                    titles.AddRange(added);
            return titles;
        }

        var index = plan.Summary.IndexOf(StepsMarker, StringComparison.Ordinal);
        if (index < 0)
            return titles;

        foreach (var title in plan.Summary[(index + StepsMarker.Length)..]
                     .Split(" | ", StringSplitOptions.RemoveEmptyEntries))
            titles.Add(title.Trim());

        return titles;
    }

    /// <summary>
    /// What to call the one card of a quick action. The orchestrator announces it as
    /// "Quick action: &lt;title&gt;"; failing that the run's own title, and failing that the same
    /// placeholder the live view uses before it knows better.
    /// </summary>
    private static string QuickActionTitle(RunRecord record)
    {
        foreach (var e in record.Events)
            if (e.Kind == nameof(EventKind.Routed)
                && e.Summary.StartsWith(QuickActionMarker, StringComparison.Ordinal)
                && e.Summary[QuickActionMarker.Length..].Trim() is { Length: > 0 } title)
                return title;

        return string.IsNullOrWhiteSpace(record.Title) ? "Working" : record.Title;
    }
}
