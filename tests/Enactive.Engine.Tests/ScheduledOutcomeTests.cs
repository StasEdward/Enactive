namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Xunit;

/// <summary>
/// How a scheduled run reports back.
///
/// <para>The console prints a report, and for a scheduled run that report goes to a window nobody
/// opened. Everything here is about the copy that OUTLIVES the process: what the Inbox says, and
/// whether it says anything at all in the cases where there is nothing good to say.</para>
/// </summary>
public sealed class ScheduledOutcomeTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid TheSchedule = Guid.NewGuid();

    private static RunRecord Record(
        RunOutcomeKind outcome, string? reason = null,
        string[]? artifacts = null, string[]? decisions = null)
    {
        var at = DateTimeOffset.Now;
        var events = new List<RunEventRecord>
        {
            new(at.AddSeconds(30),
                outcome == RunOutcomeKind.Completed
                    ? nameof(EventKind.TaskCompleted)
                    : nameof(EventKind.TaskFailed),
                outcome.ToString(),
                null, WorkEventPayload.OutcomePayload(outcome, reason))
        };

        return new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), "whatever the task was called", "gemma4-12b",
            at, at.AddSeconds(30), outcome.ToString(),
            events, artifacts ?? Array.Empty<string>(), decisions ?? Array.Empty<string>());
    }

    private static InboxItem Outcome(string name, RunRecord? record, Guid? schedule = null)
        => ScheduledOutcome.For(Workspace, schedule ?? TheSchedule, name, record, DateTimeOffset.UtcNow);

    // ── the ordinary case ───────────────────────────────────────────────────

    /// <summary>
    /// The SCHEDULE'S name, not the task's. A person recognises the thing they set up; the goal text
    /// is what they wrote once and have not read since.
    /// </summary>
    [Fact]
    public void The_item_is_titled_with_the_schedule()
    {
        var item = Outcome("Nightly dependency check", Record(RunOutcomeKind.Completed));

        Assert.Equal("Nightly dependency check", item.Title);
        Assert.Equal("result", item.Kind);
        Assert.Equal("unread", item.Status);
        Assert.Equal(Workspace, item.WorkspaceId);
    }

    /// <summary>
    /// The run id travels with the item, or the Inbox line is a dead end: "Failed" with no way to
    /// open what failed is a notification, not a report.
    /// </summary>
    [Fact]
    public void The_item_points_at_the_run()
    {
        var record = Record(RunOutcomeKind.Completed);
        var item = Outcome("nightly", record);

        Assert.Equal(record.RunId, item.RunId);
    }

    /// <summary>The reason the engine recorded is the reason the Inbox shows — one sentence, one source.</summary>
    [Fact]
    public void A_failure_carries_the_reason_the_engine_recorded()
    {
        var item = Outcome("nightly", Record(RunOutcomeKind.Failed, "the build server refused the connection"));

        Assert.Equal("error", item.Kind);
        Assert.Contains("the build server refused the connection", item.Summary);
    }

    /// <summary>Counts a person can act on: what came out of it, and what it wanted permission for.</summary>
    [Fact]
    public void The_summary_counts_artifacts_and_decisions()
    {
        var item = Outcome("nightly",
            Record(RunOutcomeKind.Completed,
                   artifacts: new[] { "a.md", "b.md" },
                   decisions: new[] { "delete the folder?" }));

        Assert.Contains("2 artifact(s)", item.Summary);
        Assert.Contains("1 decision(s)", item.Summary);
        Assert.Equal("decision", item.Kind);
    }

    // ── the cases with nothing good to say ──────────────────────────────────

    /// <summary>
    /// No record at all. The run never got as far as being written, or the history could not be
    /// read — and this is the case a report built from a record cannot describe, so it is the one
    /// most likely to end in silence. Silence is what the Inbox exists to prevent.
    /// </summary>
    [Fact]
    public void A_run_that_left_no_record_still_reaches_the_inbox()
    {
        var item = Outcome("nightly", record: null);

        Assert.Equal("error", item.Kind);
        Assert.Equal("nightly", item.Title);
        Assert.Contains("no record", item.Summary);
    }

    /// <summary>
    /// A schedule whose template has been deleted, or whose parameters no longer resolve. There is
    /// no run and no run id; without this the failure is an exit code in a scheduler's log, which is
    /// to say nowhere.
    /// </summary>
    [Fact]
    public void A_schedule_that_could_not_start_reaches_the_inbox_with_the_reason()
    {
        var item = ScheduledOutcome.CouldNotStart(
            Workspace, TheSchedule, "nightly", "there is no template 'tidy' any more", DateTimeOffset.UtcNow);

        Assert.Equal("error", item.Kind);
        Assert.Equal("nightly", item.Title);
        Assert.Contains("there is no template 'tidy' any more", item.Summary);
        Assert.Equal(Guid.Empty, item.RunId);
    }

    // ── which schedule it belongs to ────────────────────────────────────────

    /// <summary>
    /// Every item a schedule produces carries the schedule's ID, including the two above that have
    /// no run at all. Those are the ones somebody goes looking for.
    /// </summary>
    [Fact]
    public void Every_item_a_schedule_produces_names_the_schedule()
    {
        Assert.Equal(TheSchedule, Outcome("nightly", Record(RunOutcomeKind.Completed)).ScheduleId);
        Assert.Equal(TheSchedule, Outcome("nightly", record: null).ScheduleId);
        Assert.Equal(TheSchedule, ScheduledOutcome.CouldNotStart(
            Workspace, TheSchedule, "nightly", "gone", DateTimeOffset.UtcNow).ScheduleId);
    }

    /// <summary>
    /// The outcomes are found by ID, not by title. The title is a schedule's NAME, and the name is
    /// the one thing about a schedule a person is expected to change: matching on it would report
    /// that a schedule renamed this morning has never run.
    /// </summary>
    [Fact]
    public void Outcomes_survive_the_schedule_being_renamed()
    {
        var inbox = new[]
        {
            Outcome("Nightly check", Record(RunOutcomeKind.Completed)),
            Outcome("Nightly dependency check", Record(RunOutcomeKind.Failed, "fell over"))
        };

        var found = ScheduledOutcome.Of(inbox, TheSchedule);

        Assert.Equal(2, found.Count);
    }

    /// <summary>Another schedule's runs are not this schedule's history, whatever they are called.</summary>
    [Fact]
    public void Another_schedules_outcomes_are_not_returned()
    {
        var other = Guid.NewGuid();
        var inbox = new[]
        {
            Outcome("nightly", Record(RunOutcomeKind.Completed)),
            Outcome("nightly", Record(RunOutcomeKind.Completed), schedule: other),
            // An item from before any of this: no schedule at all. It must not be swept in as one.
            new InboxItem(Guid.NewGuid(), Workspace, "result", "nightly", "old", Guid.NewGuid(),
                          "read", DateTimeOffset.UtcNow)
        };

        var found = ScheduledOutcome.Of(inbox, TheSchedule);

        Assert.Single(found);
        Assert.All(found, i => Assert.Equal(TheSchedule, i.ScheduleId));
    }

    /// <summary>Newest first, and only the last few: this is a glance, not an audit.</summary>
    [Fact]
    public void The_outcomes_are_the_most_recent_first()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-10);
        var inbox = Enumerable.Range(0, 8)
            .Select(i => ScheduledOutcome.For(
                Workspace, TheSchedule, $"run {i}", Record(RunOutcomeKind.Completed), start.AddDays(i)))
            .ToArray();

        var found = ScheduledOutcome.Of(inbox, TheSchedule, most: 3);

        Assert.Equal(3, found.Count);
        Assert.Equal("run 7", found[0].Title);
        Assert.Equal("run 5", found[2].Title);
    }

    // ── the shared rule ─────────────────────────────────────────────────────

    /// <summary>
    /// A CANCELLED run is not a result. It used to file itself as one: the runner's list of endings
    /// worth flagging held "Failed" and "Incomplete" and not the third, so a job that was stopped
    /// part-way showed up beside the ones that finished. Both paths read this rule now, so fixing it
    /// in one place fixes it in both — which is the reason the rule left the runner.
    /// </summary>
    [Fact]
    public void A_cancelled_run_is_not_filed_as_a_result()
    {
        Assert.Equal("error", InboxLines.For("Cancelled", artifacts: 0, decisions: 0, reason: null).Kind);
        Assert.Equal("error", Outcome("nightly", Record(RunOutcomeKind.Cancelled)).Kind);
    }

    /// <summary>
    /// The outcome is read from the terminal event's PAYLOAD, not from the status column and not
    /// from any wording. A record whose column disagrees with what the engine recorded is exactly
    /// the case where the difference matters.
    /// </summary>
    [Fact]
    public void The_outcome_is_read_as_a_value_not_from_the_status_column()
    {
        var record = Record(RunOutcomeKind.Failed, "it fell over") with { Status = "Completed" };
        var item = Outcome("nightly", record);

        Assert.Equal("error", item.Kind);
        Assert.StartsWith("Failed", item.Summary);
    }
}
