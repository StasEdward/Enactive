namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Remote.Contracts;
using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// Every <see cref="RunOutcomeKind"/> is placed, on purpose, by every consumer that has to tell outcomes apart - the
/// exit code a scheduler reads, the Inbox line, the phone's ending, the step cards a run ends with. Outcomes are stored
/// by name, so a new one costs nothing on disk; what it costs is these places, and each one that falls through to a
/// default says something untrue about it (NeedsUser and Blocked both did, before RunHousekeeping and RunStanding were
/// walked). RunHousekeepingTests and RunStandingTests walk the other two.
/// </summary>
public sealed class EveryOutcomeIsPlacedTests
{
    /// <summary>What each consumer says of each outcome. A new outcome is a new line here, decided - not inherited.</summary>
    private static readonly Dictionary<RunOutcomeKind, (int Exit, string InboxKind, RemoteEventKind Remote)> Placed = new()
    {
        [RunOutcomeKind.Completed] = (0, "result", RemoteEventKind.Completed),
        [RunOutcomeKind.Failed] = (1, "error", RemoteEventKind.Failed),
        [RunOutcomeKind.Incomplete] = (2, "error", RemoteEventKind.Incomplete),
        [RunOutcomeKind.Cancelled] = (130, "error", RemoteEventKind.Cancelled),
        [RunOutcomeKind.NeedsUser] = (3, "decision", RemoteEventKind.Incomplete),
        [RunOutcomeKind.Blocked] = (4, "error", RemoteEventKind.Incomplete),
        // Nothing went wrong, and nothing is finished: its own exit code, its own Inbox kind, unfinished on a phone.
        [RunOutcomeKind.Paused] = (5, "paused", RemoteEventKind.Incomplete)
    };

    [Fact]
    public void Every_outcome_is_placed_by_every_consumer()
    {
        foreach (var outcome in Enum.GetValues<RunOutcomeKind>())
        {
            Assert.True(Placed.TryGetValue(outcome, out var expected),
                $"{outcome} is a new outcome: place it in RunReport.ExitCodeFor, InboxLines, EventMapping and RunFeed, then here.");
            Assert.Equal(expected.Exit, RunReport.ExitCodeFor(outcome));
            Assert.Equal(expected.InboxKind, InboxLines.For(outcome.ToString(), 0, 0, null).Kind);
            var ending = EventMapping.Ending(Terminal(outcome, "why"));
            Assert.NotNull(ending);
            Assert.Equal(expected.Remote, ending!.Value.Kind);
            Assert.DoesNotContain("ended without recording how", ending.Value.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_outcome_has_an_exit_code_of_its_own()
        => Assert.Equal(Enum.GetValues<RunOutcomeKind>().Length,
            Enum.GetValues<RunOutcomeKind>().Select(RunReport.ExitCodeFor).Distinct().Count());

    /// <summary>The Inbox line of a paused run: what it is, and where it stopped - not an error and not a result.</summary>
    [Fact]
    public void A_paused_run_is_filed_in_the_inbox_as_paused_with_where_it_stopped()
    {
        var (_, reason) = RunOutcomeDecision.Paused(2, 4, 0, null);
        var line = InboxLines.For(nameof(RunOutcomeKind.Paused), 2, 0, reason);

        Assert.Equal("paused", line.Kind);
        // Said once: it read "Paused · Paused at step 2 of 4".
        Assert.Equal("Paused at step 2 of 4: 2 step(s) settled, 2 left - resume the run to carry on with them. · 2 artifact(s) so far",
            line.Summary);
        Assert.Equal("Paused", InboxLines.For(nameof(RunOutcomeKind.Paused), 0, 0, null).Summary);
        Assert.Equal("Paused · somebody asked", InboxLines.For(nameof(RunOutcomeKind.Paused), 0, 0, "somebody asked").Summary);
    }

    /// <summary>Where a pause stopped, in words: before the first step is not "step 0", and blocked steps left are named.</summary>
    [Fact]
    public void A_pause_says_where_it_stopped_in_words_a_person_reads()
    {
        Assert.Equal("Paused before the first of 4 step(s): 4 left - resume the run to carry on with them.",
            RunOutcomeDecision.Paused(0, 4, 0, null).Reason);
        Assert.Equal("Paused at step 1 of 3: 1 step(s) settled, 2 left, 1 of them blocked - tried again when the run is resumed - "
                     + "resume the run to carry on with them.", RunOutcomeDecision.Paused(1, 3, 1, null).Reason);
        Assert.Equal(RunOutcomeKind.Paused, RunOutcomeDecision.Paused(1, 3, 1, null).Outcome);
        Assert.All(new[]
        {
            RunOutcomeDecision.Paused(1, 3, 0, "full", keepsCheckpoints: false),
            RunOutcomeDecision.Paused(1, 3, 0, "full", boundaryKept: true),
            RunOutcomeDecision.Paused(1, 3, 0, "full", boundaryKept: false)
        }, p => Assert.Equal(RunOutcomeKind.Incomplete, p.Outcome));
    }

    /// <summary>A schedule's run that was paused is not finished: not a result in the schedule's past, and Unfinished in the runs list.</summary>
    [Fact]
    public void A_schedule_does_not_treat_a_paused_run_as_finished()
    {
        var at = DateTimeOffset.UtcNow;
        var record = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "Nightly books", null, at, at, nameof(RunOutcomeKind.Paused),
            [new RunEventRecord(at, nameof(EventKind.TaskFailed), "Paused: Paused at step 2 of 4",
                null, WorkEventPayload.OutcomePayload(RunOutcomeKind.Paused, "Paused at step 2 of 4: 2 step(s) left."))],
            [], []);

        var item = ScheduledOutcome.For(Guid.NewGuid(), Guid.NewGuid(), "Nightly books", record, at);

        Assert.Equal("paused", item.Kind);
        Assert.NotEqual("result", item.Kind);
        Assert.Contains("Paused at step 2 of 4", item.Summary, StringComparison.Ordinal);
        Assert.True(RunHousekeeping.Matches(RunSummary.Of(record), RunFilter.Unfinished, at));
        Assert.False(RunHousekeeping.Matches(RunSummary.Of(record), RunFilter.Completed, at));
    }

    /// <summary>The run's cards: a step a paused run never reached was not finished wrongly - it is still to come.</summary>
    [Fact]
    public void A_paused_runs_unreached_steps_are_still_to_come_not_failed()
    {
        var feed = new RunFeed();
        var runId = Guid.NewGuid();
        feed.Apply(new WorkEvent(Guid.NewGuid(), Guid.Empty, runId, DateTimeOffset.UtcNow, EventKind.PlanCreated,
            "Books — 2 steps: Count the invoices | Write the summary",
            WorkEventPayload.PlanPayload("Books", ["Count the invoices", "Write the summary"])));
        feed.Apply(new WorkEvent(Guid.NewGuid(), Guid.Empty, runId, DateTimeOffset.UtcNow, EventKind.StepStarted,
            "[1/2] Count the invoices", null), 1);
        feed.Apply(new WorkEvent(Guid.NewGuid(), Guid.Empty, runId, DateTimeOffset.UtcNow, EventKind.StepCompleted,
            "[1/2] Count the invoices — Done", WorkEventPayload.StepPayload(1, StepOutcomeKind.Succeeded)), 1);
        feed.Apply(Terminal(RunOutcomeKind.Paused, "Paused at step 1 of 2"));

        var cards = feed.Cards;
        Assert.Equal(FeedCardStatus.Done, cards[0].Status);
        Assert.Equal(FeedCardStatus.Pending, cards[1].Status);
        Assert.DoesNotContain("Never finished", cards[1].Activity, StringComparison.Ordinal);
    }

    private static WorkEvent Terminal(RunOutcomeKind outcome, string reason)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            outcome == RunOutcomeKind.Completed ? EventKind.TaskCompleted : EventKind.TaskFailed,
            $"{outcome}: {reason}", WorkEventPayload.OutcomePayload(outcome, reason));
}
