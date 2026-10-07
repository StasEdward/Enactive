namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// A run's step cards, folded from its events - the same fold for the run happening in front of you and for one
/// reopened from the history (RunReplayTests asks the second).
///
/// <para>Until 2026-10-08 the live window and the replay each had an interpreter of their own, kept in agreement by
/// comments, and they had drifted - see RunFeed for how. These ask what only a fold of its own could get right.</para>
/// </summary>
public sealed class RunFeedTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent Step(EventKind kind, int step, string summary, string? payload = null)
        => Ev(kind, summary, payload ?? $"{{\"step\":{step}}}");

    private static RunFeed Fold(IEnumerable<WorkEvent> events, string? title = null)
    {
        var feed = new RunFeed(title);
        foreach (var ev in events)
            feed.Apply(ev);
        return feed;
    }

    private static readonly WorkEvent[] DiskRun =
    [
        Ev(EventKind.IntentReceived, "Intent: check the disks"),
        Ev(EventKind.Routed, "Worker 'Ops' -> model ollama/qwen", WorkEventPayload.RoutePayload("worker", "ollama", "qwen")),
        Ev(EventKind.PlanCreated, "Disks — 2 steps: check the disks | write the report",
            WorkEventPayload.PlanPayload("Disks", ["check the disks", "write the report"])),
        Step(EventKind.StepStarted, 1, "[1/2] check the disks"),
        Step(EventKind.AssistantDelta, 1, "Checking C: first."),
        Step(EventKind.ToolInvoked, 1, """run_command {"command":"chkdsk C:"}"""),
        Step(EventKind.ToolResult, 1, "run_command -> ok: no problems found"),
        Ev(EventKind.DecisionResolved, "send_email: denied", WorkEventPayload.DecisionPayload(1, "send_email", allowed: false)),
        Step(EventKind.StepCompleted, 1, "[1/2] check the disks — done", WorkEventPayload.StepPayload(1, StepOutcomeKind.Succeeded)),
        Step(EventKind.StepStarted, 2, "[2/2] write the report"),
        Step(EventKind.ToolInvoked, 2, """write_file {"path":"disks.md"}"""),
        Step(EventKind.StepCompleted, 2, "[2/2] write the report", WorkEventPayload.StepPayload(2, StepOutcomeKind.Incomplete, "the report was cut off")),
        Ev(EventKind.TaskFailed, "Incomplete: 1 step(s) did not finish", WorkEventPayload.OutcomePayload(RunOutcomeKind.Incomplete, "1 step(s) did not finish"))
    ];

    /// <summary>
    /// The guarantee the fold exists for: the same events, folded one at a time as they arrive and read back from
    /// the run's record, draw the same cards.
    /// </summary>
    [Fact]
    public void A_run_reads_the_same_live_and_reopened()
    {
        var live = Fold(DiskRun, "check the disks");
        var record = new RunRecord(Run, Guid.NewGuid(), "check the disks", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "Incomplete", DiskRun.Select(e => new RunEventRecord(e.At, e.Kind.ToString(), e.Summary, e.StepNo(), e.PayloadJson)).ToArray(),
            [], []);

        var reopened = RunFeed.Replay(record);

        Assert.Equal(Draw(live), Draw(reopened));
        Assert.Equal(new[] { "check the disks", "write the report" }, live.Cards.Select(c => c.Title));
        Assert.Equal("Incomplete — the report was cut off", live.Cards[1].Activity);
    }

    private static string[] Draw(RunFeed feed)
        => feed.Cards.Select(c => $"{c.Title}|{c.Status}|{c.Activity}|{c.Tally}|"
                                  + string.Join(";", c.Entries.Select(e => $"{e.Kind}:{e.Label}:{e.Detail}"))).ToArray();

    /// <summary>
    /// A run that stops while two steps run at once: the window marked only the "current" card, and with two in
    /// flight there is none, so both stayed "running…" for good while the reopened run said they never finished.
    /// </summary>
    [Fact]
    public void A_run_that_stops_with_steps_running_says_each_never_finished()
    {
        var feed = Fold([
            Ev(EventKind.PlanCreated, "Disks — 2 steps: check C: | check D:", WorkEventPayload.PlanPayload("Disks", ["check C:", "check D:"])),
            Step(EventKind.StepStarted, 1, "[1/2] check C:"),
            Step(EventKind.StepStarted, 2, "[2/2] check D:")
        ]);

        feed.Stopped();

        Assert.All(feed.Cards, card =>
        {
            Assert.Equal(FeedCardStatus.Failed, card.Status);
            Assert.Equal("Never finished — the run ended here", card.Activity);
        });
    }

    [Fact]
    public void A_quick_action_is_named_by_its_title_s_value_and_not_by_the_sentence()
    {
        var feed = Fold([Ev(EventKind.Routed, "Quick action: an older wording", WorkEventPayload.QuickActionPayload("check the disks"))]);

        Assert.Equal("check the disks", feed.Title);
    }

    [Fact]
    public void Planning_is_read_from_the_route_s_value()
    {
        var feed = Fold([Ev(EventKind.Routed, "the worker is routed", WorkEventPayload.RoutePayload("worker", "ollama", "qwen"))]);

        Assert.Equal("Planning", feed.Phase);
    }

    [Fact]
    public void A_reply_streamed_before_a_tool_call_is_one_note_before_it()
    {
        var card = Fold(DiskRun.Take(6)).Cards[0];

        Assert.Equal(new[] { FeedEntryKind.Note, FeedEntryKind.Command }, card.Entries.Select(e => e.Kind));
        Assert.Equal("Checking C: first.", card.Entries[0].Label);
    }

    [Fact]
    public void A_refused_call_is_its_own_line_and_counted_apart()
    {
        var card = Fold(DiskRun.Take(9)).Cards[0];

        Assert.Contains(card.Entries, e => e.Kind == FeedEntryKind.Refusal && e.Label == "send_email: denied");
        Assert.Contains("refused", card.Tally);
    }

    [Fact]
    public void A_card_waits_for_a_person_and_runs_again_when_answered()
    {
        var feed = Fold([
            Ev(EventKind.PlanCreated, "Mail — 1 steps: send it", WorkEventPayload.PlanPayload("Mail", ["send it"])),
            Step(EventKind.StepStarted, 1, "[1/1] send it"),
            Ev(EventKind.DecisionRequested, "Approve tool 'send_email'?", "{\"step\":1}")
        ]);
        var card = feed.Cards[0];
        Assert.True(card.WaitingForYou);
        Assert.Equal("Waiting for your approval…", card.Activity);

        feed.Apply(Ev(EventKind.DecisionResolved, "send_email: allowed", WorkEventPayload.DecisionPayload(1, "send_email", allowed: true)));

        Assert.False(card.WaitingForYou);
        Assert.Equal(FeedCardStatus.Running, card.Status);
    }

    /// <summary>A view inserts each new card where the feed put it - the grown steps under their step, in order.</summary>
    [Fact]
    public void New_cards_come_with_where_they_are_shown()
    {
        var feed = Fold([
            Ev(EventKind.PlanCreated, "Disks — 2 steps: list | report", WorkEventPayload.PlanPayload("Disks", ["list", "report"]))
        ]);

        var added = feed.Apply(Step(EventKind.PlanExpanded, 1, "[1/4] list — 2 step(s)", WorkEventPayload.PlanPayload("list", ["C:", "D:"])
            .Replace("{", "{\"step\":1,", StringComparison.Ordinal)));

        Assert.Equal(new[] { ("C:", 1), ("D:", 2) }, added.Select(a => (a.Card.Title, a.At)));
        Assert.Equal(new[] { "list", "C:", "D:", "report" }, feed.Cards.Select(c => c.Title));
        Assert.Equal("2 item step(s); joins their results when they have all ended", feed.Cards[0].Activity);
    }
}
