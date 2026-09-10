namespace Enactive.Core.Inbox;

using Enactive.Core.History;

/// <summary>
/// What a person is told about a run that fired from a SCHEDULE.
///
/// <para>A scheduled run's audience is somebody reading afterwards. The console prints a report, but
/// a scheduled run's console is a window nobody opened, and its stdout is gone by the time anyone
/// looks — so the report reaching stdout is not the same as the result reaching a person. The Inbox
/// is the part that survives.</para>
///
/// <para>The title is the SCHEDULE'S name, not the task's: what a person recognises at a glance is
/// the thing they set up, and "Nightly dependency check" says which of their schedules this was in a
/// way that the goal text does not.</para>
/// </summary>
public static class ScheduledOutcome
{
    /// <param name="record">
    /// The run as it was written to the history, or null when there is none — the run never got far
    /// enough to be recorded, or the history could not be read. That case gets an item of its own
    /// rather than silence: a schedule that produces nothing at all is exactly what somebody needs
    /// telling about, and it is the case a report built from a record cannot describe.
    /// </param>
    public static InboxItem For(Guid workspaceId, string scheduleName, RunRecord? record, DateTimeOffset at)
    {
        if (record is null)
            return new InboxItem(
                Guid.NewGuid(), workspaceId, "error", scheduleName,
                "The scheduled run left no record — nothing reached the history.",
                Guid.Empty, "unread", at);

        var line = InboxLines.For(
            RunReport.OutcomeOf(record)?.ToString() ?? record.Status,
            record.Artifacts.Count,
            record.Decisions.Count,
            RunReport.ReasonOf(record));

        return new InboxItem(
            Guid.NewGuid(), workspaceId, line.Kind, scheduleName, line.Summary,
            record.RunId, "unread", at);
    }

    /// <summary>
    /// The item for a schedule that could not even be turned into a task — a template that has been
    /// deleted, or one whose parameters no longer resolve.
    ///
    /// <para>Its own entry point because there is no run and no run id, and the reason is the whole
    /// message. Without it this failure is an exit code into a scheduler's log, which is to say
    /// nowhere.</para>
    /// </summary>
    public static InboxItem CouldNotStart(Guid workspaceId, string scheduleName, string why, DateTimeOffset at)
        => new(Guid.NewGuid(), workspaceId, "error", scheduleName,
               $"Did not run · {why}", Guid.Empty, "unread", at);
}
