namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// What a finished run's Execution view is made of, decided from the record alone.
///
/// <para>Replay attributed events to steps by the step number stored on each event — right, and
/// nothing is ever guessed from order — and then assumed every run had steps. A QUICK ACTION has no
/// plan and no steps, so none of its events carries a number, and the rebuild produced nothing: an
/// empty tab under "This run was recorded before step numbers were". The record was current; the run
/// simply never had steps. Since the planner is told to strongly prefer quick actions, that was most
/// runs — the cards were there while it ran and gone the moment you opened anything else.</para>
///
/// <para>Since 2026-10-08 the record is folded by the same RunFeed the live window folds its events
/// through, so these are the live view's cards as well.</para>
/// </summary>
public sealed class RunReplayTests
{
    private static RunEventRecord Ev(string kind, string summary, int? step = null, string? payload = null)
        => new(DateTimeOffset.UtcNow, kind, summary, step, payload);

    private static RunRecord Record(params RunEventRecord[] events)
        => new(RunId: Guid.NewGuid(),
               TaskId: Guid.NewGuid(),
               Title: "запусти тесты",
               Model: "gemma4-12b:latest",
               StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
               FinishedAt: DateTimeOffset.UtcNow,
               Status: "Completed",
               Events: events,
               Artifacts: Array.Empty<string>(),
               Decisions: Array.Empty<string>());

    private static IReadOnlyList<FeedCard> Cards(RunRecord record) => RunFeed.Replay(record).Cards;

    // ── the run from the screenshot ───────────────────────────────────────────────────

    [Fact]
    public void A_quick_action_replays_as_one_card_carrying_its_work()
    {
        var record = Record(
            Ev(nameof(EventKind.IntentReceived), "Intent: запусти тесты"),
            Ev(nameof(EventKind.Routed), "Worker 'Developer' -> model ollama/gemma4-12b:latest"),
            Ev(nameof(EventKind.Routed), "Quick action: run the tests", payload: WorkEventPayload.QuickActionPayload("run the tests")),
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""),
            Ev(nameof(EventKind.ToolResult), "run_command -> ok: 202 passed"),
            Ev(nameof(EventKind.TaskCompleted), "nothing written",
               payload: WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)));

        var card = Assert.Single(Cards(record));

        Assert.Equal("run the tests", card.Title);
        Assert.Equal(FeedCardStatus.Done, card.Status);
        var ran = Assert.Single(card.Entries);
        Assert.Equal((FeedEntryKind.Command, "Ran: dotnet test"), (ran.Kind, ran.Label));
        Assert.Equal("run_command -> ok: 202 passed", ran.Detail);
    }

    // Done, but the engine saw one of its own checks fail: reopened, the run must still say so.
    [Fact]
    public void A_completed_quick_action_with_something_to_add_says_it()
    {
        const string reason = "Completed, but check 'No new build errors' failed: CS0103 in Mail.cs";
        var record = Record(
            Ev(nameof(EventKind.Routed), "Quick action: add the mail module", payload: WorkEventPayload.QuickActionPayload("add the mail module")),
            Ev(nameof(EventKind.ToolInvoked), """write_file {"path":"Mail.cs"}"""),
            Ev(nameof(EventKind.TaskCompleted), "Mail.cs written",
               payload: WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed, reason)));

        var card = Assert.Single(Cards(record));

        Assert.Equal(FeedCardStatus.Done, card.Status);
        Assert.Equal(reason, card.Activity);
    }

    // A run that went wrong has to survive the rebuild as one, or the history quietly launders it.
    [Fact]
    public void A_quick_action_that_failed_replays_as_failed_and_says_why()
    {
        var record = Record(
            Ev(nameof(EventKind.Routed), "Quick action: run the tests"),
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""),
            Ev(nameof(EventKind.TaskFailed), "Incomplete: the context window filled up",
               payload: WorkEventPayload.OutcomePayload(RunOutcomeKind.Incomplete, "the context window filled up")));

        var card = Assert.Single(Cards(record));

        Assert.Equal(FeedCardStatus.Failed, card.Status);
        Assert.Equal("Incomplete — the context window filled up", card.Activity);
    }

    // Cut off before it could finish: no outcome at all, which is not the same as success and must
    // not be drawn as one.
    [Fact]
    public void A_quick_action_with_no_terminal_event_never_finished()
    {
        var record = Record(
            Ev(nameof(EventKind.Routed), "Quick action: run the tests"),
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""));

        var card = Assert.Single(Cards(record));

        Assert.Equal(FeedCardStatus.Failed, card.Status);
        Assert.Equal("Never finished — the run ended here", card.Activity);
    }

    [Fact]
    public void A_quick_action_without_an_announced_title_falls_back_to_the_runs_own()
    {
        var record = Record(
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""),
            Ev(nameof(EventKind.TaskCompleted), "done"));

        Assert.Equal("запусти тесты", Assert.Single(Cards(record)).Title);
    }

    /// <summary>A record from before the quick action's title was a value still reads it from the sentence.</summary>
    [Fact]
    public void A_quick_action_title_recorded_only_as_wording_is_still_read()
    {
        var record = Record(
            Ev(nameof(EventKind.Routed), "Quick action: run the tests"),
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""),
            Ev(nameof(EventKind.TaskCompleted), "done"));

        Assert.Equal("run the tests", Assert.Single(Cards(record)).Title);
    }

    // ── planned runs ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The steps a plan grew while it ran (Phase 5.3) are numbered after the ones it had, and SHOWN under the step
    /// they were made from - as the live window shows them. Reopened, they used to drop to the end of the list.
    /// </summary>
    [Fact]
    public void Steps_the_plan_grew_are_shown_under_the_step_they_were_made_from()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Disks — 2 steps: list the disks | write the report",
               payload: WorkEventPayload.PlanPayload("Disks", ["list the disks", "write the report"])),
            Ev(nameof(EventKind.StepStarted), "[1/2] list the disks", 1),
            Ev(nameof(EventKind.PlanExpanded), "[1/4] list the disks — 2 step(s) for 2 item(s)", 1,
               WorkEventPayload.PlanPayload("list the disks", ["check C:", "check D:"])),
            Ev(nameof(EventKind.StepStarted), "[3/4] check C:", 3),
            Ev(nameof(EventKind.StepCompleted), "[3/4] check C: — done", 3, WorkEventPayload.StepPayload(3, StepOutcomeKind.Succeeded)));

        var cards = Cards(record);

        Assert.Equal(new[] { "list the disks", "check C:", "check D:", "write the report" }, cards.Select(c => c.Title).ToArray());
        Assert.Same(cards[0], cards[1].Parent);
        Assert.Equal(FeedCardStatus.Done, cards[1].Status);
    }

    [Fact]
    public void A_planned_run_replays_one_card_per_step_with_its_own_events()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Two things — 2 steps: first thing | second thing"),
            Ev(nameof(EventKind.StepStarted), "[1/2] first thing", 1),
            Ev(nameof(EventKind.ToolInvoked), """write_file {"path":"a.txt"}""", 1),
            Ev(nameof(EventKind.StepCompleted), "[1/2] first thing — done", 1,
               WorkEventPayload.StepPayload(1, StepOutcomeKind.Succeeded)),
            Ev(nameof(EventKind.StepStarted), "[2/2] second thing", 2),
            Ev(nameof(EventKind.StepCompleted), "[2/2] second thing — skipped (a dependency did not succeed)", 2,
               WorkEventPayload.StepPayload(2, StepOutcomeKind.Skipped)));

        var cards = Cards(record);

        Assert.Equal(new[] { "first thing", "second thing" }, cards.Select(c => c.Title).ToArray());
        Assert.Equal(new[] { FeedCardStatus.Done, FeedCardStatus.Skipped }, cards.Select(c => c.Status).ToArray());

        // Step 1's tool call belongs to step 1 and to nothing else.
        Assert.Contains(cards[0].Entries, e => e.Label == "Wrote a.txt");
        Assert.Empty(cards[1].Entries);
    }

    // The wording fallback for a record an earlier build wrote, before the outcome was a value.
    [Fact]
    public void A_step_outcome_recorded_only_as_wording_is_still_read()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "One thing — 1 steps: only thing"),
            Ev(nameof(EventKind.StepCompleted), "[1/1] only thing — FAILED: it did not work", 1));

        Assert.Equal(FeedCardStatus.Failed, Assert.Single(Cards(record)).Status);
    }

    // ── and the case the old message was actually written for ─────────────────────────

    // A plan whose events carry no step numbers. There is no honest way to say which card a tool
    // call belonged to, so it builds none and the timeline is the whole story.
    [Fact]
    public void A_planned_run_recorded_without_step_numbers_builds_nothing()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Two things — 2 steps: first thing | second thing"),
            Ev(nameof(EventKind.ToolInvoked), """write_file {"path":"a.txt"}"""),
            Ev(nameof(EventKind.TaskCompleted), "done"));

        Assert.Empty(Cards(record));
    }

    [Fact]
    public void A_run_with_no_visible_work_builds_nothing()
    {
        var record = Record(
            Ev(nameof(EventKind.IntentReceived), "Intent: hello"),
            Ev(nameof(EventKind.Routed), "Quick action: hello"),
            Ev(nameof(EventKind.TaskCompleted), "nothing written"));

        Assert.Empty(Cards(record));
    }

    // ── why a planned step ended the way it did ──────────────────────────────────────
    //
    // A planned step's reason lived in the summary sentence, so the run from 2026-09-11 replayed its
    // failed step as the bare word "Incomplete" — the one card a person opens the run to look at,
    // saying nothing about what happened.

    [Fact]
    public void A_planned_step_that_did_not_finish_says_why()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Review the diff — 2 steps: get the diff | write it up"),
            Ev(nameof(EventKind.ToolInvoked), """git {"args":["diff"]}""", step: 1),
            Ev(nameof(EventKind.StepCompleted),
               "[1/2] get the diff — INCOMPLETE: unresolved tool call: git — the user did not permit this action",
               step: 1,
               payload: WorkEventPayload.StepPayload(
                   1, StepOutcomeKind.Incomplete,
                   "unresolved tool call: git — the user did not permit this action")),
            Ev(nameof(EventKind.StepCompleted), "[2/2] write it up — skipped (a dependency did not succeed)",
               step: 2, payload: WorkEventPayload.StepPayload(2, StepOutcomeKind.Skipped)));

        var cards = Cards(record);

        Assert.Equal(FeedCardStatus.Failed, cards[0].Status);
        // With the outcome word in front of it - once: "the user did not permit this action" alone does
        // not say whether the step failed or merely stopped, and those are different cards.
        Assert.Equal("Incomplete — unresolved tool call: git — the user did not permit this action", cards[0].Activity);
    }

    /// <summary>
    /// The live card's words, not the enum's. Reopened, a blocked step said "Blocked — Blocked — …" and a step with
    /// no reason said "ReviewRejected", where the live card had said "Blocked — …" and "Rejected by the reviewer".
    /// </summary>
    [Theory]
    [InlineData(StepOutcomeKind.Blocked, "needs a permission it was refused: send_email", "Blocked — needs a permission it was refused: send_email")]
    [InlineData(StepOutcomeKind.DoneUnverified, "the review could not be used", "Done, not verified — the review could not be used")]
    [InlineData(StepOutcomeKind.ReviewRejected, null, "Rejected by the reviewer")]
    public void A_step_s_ending_reads_as_it_did_live(StepOutcomeKind outcome, string? reason, string says)
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Mail — 1 steps: send the report"),
            Ev(nameof(EventKind.StepStarted), "[1/1] send the report", 1),
            Ev(nameof(EventKind.StepCompleted), "[1/1] send the report", 1, WorkEventPayload.StepPayload(1, outcome, reason)));

        Assert.Equal(says, Assert.Single(Cards(record)).Activity);
    }

    /// <summary>
    /// A step that worked gets no reason line. "Why did this succeed" is not a question, and a note
    /// under every green card is a note nobody reads.
    /// </summary>
    [Fact]
    public void A_step_that_succeeded_says_done()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "One thing — 1 steps: do it"),
            Ev(nameof(EventKind.ToolInvoked), """write_file {"path":"a.txt"}""", step: 1),
            Ev(nameof(EventKind.StepCompleted), "[1/1] do it — done",
               step: 1, payload: WorkEventPayload.StepPayload(1, StepOutcomeKind.Succeeded)));

        Assert.Equal("Done", Assert.Single(Cards(record)).Activity);
    }

    /// <summary>
    /// A skipped step's reason belongs to the step that failed, not to this one. The card says
    /// "Skipped — a dependency failed" in its own words; repeating another step's failure here
    /// sends a person looking for a fault in the wrong card.
    /// </summary>
    [Fact]
    public void A_skipped_step_carries_no_reason_of_its_own()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Two — 2 steps: a | b"),
            Ev(nameof(EventKind.ToolInvoked), """git {"args":["diff"]}""", step: 1),
            Ev(nameof(EventKind.StepCompleted), "[1/2] a — FAILED: it broke",
               step: 1, payload: WorkEventPayload.StepPayload(1, StepOutcomeKind.Failed, "it broke")),
            Ev(nameof(EventKind.StepCompleted), "[2/2] b — skipped (a dependency did not succeed)",
               step: 2, payload: WorkEventPayload.StepPayload(2, StepOutcomeKind.Skipped)));

        Assert.Equal("Skipped — a dependency failed", Cards(record)[1].Activity);
    }

    /// <summary>
    /// The VALUE wins over the sentence, which is the whole reason it exists. Here the two disagree on
    /// purpose, so a test green on the fallback alone cannot pass.
    /// </summary>
    [Fact]
    public void The_recorded_reason_beats_the_sentence()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "One — 1 steps: do it"),
            Ev(nameof(EventKind.ToolInvoked), """git {"args":["diff"]}""", step: 1),
            Ev(nameof(EventKind.StepCompleted), "[1/1] do it — INCOMPLETE: an older wording",
               step: 1,
               payload: WorkEventPayload.StepPayload(1, StepOutcomeKind.Incomplete, "what actually stopped it")));

        var says = Assert.Single(Cards(record)).Activity;

        Assert.Contains("what actually stopped it", says);
        Assert.DoesNotContain("an older wording", says);
    }

    /// <summary>
    /// A run recorded before the reason was a value still says something. The fallback reads the
    /// tail of the summary, which is exactly as fragile as it looks and is never consulted for a run
    /// recorded since — the same arrangement the outcome word itself has.
    /// </summary>
    [Fact]
    public void An_older_record_falls_back_to_the_sentence()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "One — 1 steps: do it"),
            Ev(nameof(EventKind.ToolInvoked), """git {"args":["diff"]}""", step: 1),
            Ev(nameof(EventKind.StepCompleted), "[1/1] do it — INCOMPLETE: unresolved tool call: git",
               step: 1, payload: """{"step":1,"stepOutcome":"Incomplete"}"""));

        Assert.Contains("unresolved tool call", Assert.Single(Cards(record)).Activity);
    }
}
