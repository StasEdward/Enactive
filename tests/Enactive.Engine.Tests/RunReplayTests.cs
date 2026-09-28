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

    // ── the run from the screenshot ───────────────────────────────────────────────────

    [Fact]
    public void A_quick_action_replays_as_one_segment_carrying_its_work()
    {
        var record = Record(
            Ev(nameof(EventKind.IntentReceived), "Intent: запусти тесты"),
            Ev(nameof(EventKind.Routed), "Worker 'Developer' -> model ollama/gemma4-12b:latest"),
            Ev(nameof(EventKind.Routed), "Quick action: run the tests"),
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""),
            Ev(nameof(EventKind.ToolResult), "run_command -> ok: 202 passed"),
            Ev(nameof(EventKind.TaskCompleted), "nothing written",
               payload: WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)));

        var segment = Assert.Single(RunReplayPlan.Segments(record));

        Assert.Equal("run the tests", segment.Title);
        Assert.Null(segment.StepNumber);
        Assert.Equal(StepOutcomeKind.Succeeded, segment.Outcome);
        Assert.Contains(segment.Events, e => e.Kind == nameof(EventKind.ToolInvoked));

        // The terminal event is the segment's OUTCOME, not one of its entries.
        Assert.DoesNotContain(segment.Events, e => e.Kind == nameof(EventKind.TaskCompleted));
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

        var segment = Assert.Single(RunReplayPlan.Segments(record));

        Assert.Equal(StepOutcomeKind.Incomplete, segment.Outcome);
        Assert.Contains("context window", segment.Note);
    }

    // Cut off before it could finish: no outcome at all, which is not the same as success and must
    // not be drawn as one.
    [Fact]
    public void A_quick_action_with_no_terminal_event_has_no_outcome()
    {
        var record = Record(
            Ev(nameof(EventKind.Routed), "Quick action: run the tests"),
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""));

        var segment = Assert.Single(RunReplayPlan.Segments(record));

        Assert.Null(segment.Outcome);
    }

    [Fact]
    public void A_quick_action_without_an_announced_title_falls_back_to_the_runs_own()
    {
        var record = Record(
            Ev(nameof(EventKind.ToolInvoked), """run_command {"command":"dotnet test"}"""),
            Ev(nameof(EventKind.TaskCompleted), "done"));

        Assert.Equal("запусти тесты", Assert.Single(RunReplayPlan.Segments(record)).Title);
    }

    // ── planned runs are unchanged ────────────────────────────────────────────────────

    /// <summary>The steps a plan grew while it ran (Phase 5.3) are replayed too, numbered after the ones it had.</summary>
    [Fact]
    public void Steps_the_plan_grew_are_replayed_after_the_ones_it_had()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Wiki — 2 steps: list | review", payload: WorkEventPayload.PlanPayload("Wiki", ["list", "review"])),
            Ev(nameof(EventKind.PlanExpanded), "[2/3] review — 1 step(s) for 1 item(s)", payload: WorkEventPayload.PlanPayload("review", ["review: a.md"])),
            Ev(nameof(EventKind.StepStarted), "[3/3] review: a.md", 3),
            Ev(nameof(EventKind.StepCompleted), "[3/3] review: a.md — done", 3, WorkEventPayload.StepPayload(3, StepOutcomeKind.Succeeded)));

        var segments = RunReplayPlan.Segments(record);

        Assert.Equal(new[] { "list", "review", "review: a.md" }, segments.Select(s => s.Title).ToArray());
        Assert.Equal(StepOutcomeKind.Succeeded, segments[2].Outcome);
    }

    [Fact]
    public void A_planned_run_replays_one_segment_per_step_with_its_own_events()
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

        var segments = RunReplayPlan.Segments(record);

        Assert.Equal(2, segments.Count);
        Assert.Equal(new[] { "first thing", "second thing" }, segments.Select(s => s.Title).ToArray());
        Assert.Equal(new int?[] { 1, 2 }, segments.Select(s => s.StepNumber).ToArray());
        Assert.Equal(StepOutcomeKind.Succeeded, segments[0].Outcome);
        Assert.Equal(StepOutcomeKind.Skipped, segments[1].Outcome);

        // Step 1's tool call belongs to step 1 and to nothing else.
        Assert.Contains(segments[0].Events, e => e.Kind == nameof(EventKind.ToolInvoked));
        Assert.DoesNotContain(segments[1].Events, e => e.Kind == nameof(EventKind.ToolInvoked));
    }

    // The wording fallback for a record an earlier build wrote, before the outcome was a value.
    [Fact]
    public void A_step_outcome_recorded_only_as_wording_is_still_read()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "One thing — 1 steps: only thing"),
            Ev(nameof(EventKind.StepCompleted), "[1/1] only thing — FAILED: it did not work", 1));

        Assert.Equal(StepOutcomeKind.Failed, Assert.Single(RunReplayPlan.Segments(record)).Outcome);
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

        Assert.Empty(RunReplayPlan.Segments(record));
    }

    [Fact]
    public void A_run_with_no_visible_work_builds_nothing()
    {
        var record = Record(
            Ev(nameof(EventKind.IntentReceived), "Intent: hello"),
            Ev(nameof(EventKind.Routed), "Quick action: hello"),
            Ev(nameof(EventKind.TaskCompleted), "nothing written"));

        Assert.Empty(RunReplayPlan.Segments(record));
    }

    // ── why a planned step ended the way it did ──────────────────────────────────────
    //
    // A quick action has said this since it was written (see above). A planned step had not: the
    // reason lived in the summary sentence and the segment carried none, so the run from
    // 2026-09-11 replayed its failed step as the bare word "Incomplete" — the one card a person
    // opens the run to look at, saying nothing about what happened.

    [Fact]
    public void A_planned_step_that_did_not_finish_says_why()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "Review the diff — 2 steps: get the diff | write it up",
               payload: null),
            Ev(nameof(EventKind.ToolInvoked), """git {"args":["diff"]}""", step: 1),
            Ev(nameof(EventKind.StepCompleted),
               "[1/2] get the diff — INCOMPLETE: unresolved tool call: git — the user did not permit this action",
               step: 1,
               payload: WorkEventPayload.StepPayload(
                   1, StepOutcomeKind.Incomplete,
                   "unresolved tool call: git — the user did not permit this action")),
            Ev(nameof(EventKind.StepCompleted), "[2/2] write it up — skipped (a dependency did not succeed)",
               step: 2, payload: WorkEventPayload.StepPayload(2, StepOutcomeKind.Skipped)));

        var segments = RunReplayPlan.Segments(record);

        Assert.Equal(StepOutcomeKind.Incomplete, segments[0].Outcome);
        Assert.Contains("did not permit", segments[0].Note);

        // With the outcome word in front of it: "the user did not permit this action" alone does
        // not say whether the step failed or merely stopped, and those are different cards.
        Assert.StartsWith("Incomplete", segments[0].Note);
    }

    /// <summary>
    /// A step that worked gets no reason line. "Why did this succeed" is not a question, and a note
    /// under every green card is a note nobody reads.
    /// </summary>
    [Fact]
    public void A_step_that_succeeded_carries_no_reason()
    {
        var record = Record(
            Ev(nameof(EventKind.PlanCreated), "One thing — 1 steps: do it"),
            Ev(nameof(EventKind.ToolInvoked), """write_file {"path":"a.txt"}""", step: 1),
            Ev(nameof(EventKind.StepCompleted), "[1/1] do it — done",
               step: 1, payload: WorkEventPayload.StepPayload(1, StepOutcomeKind.Succeeded)));

        Assert.Null(Assert.Single(RunReplayPlan.Segments(record)).Note);
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

        Assert.Null(RunReplayPlan.Segments(record)[1].Note);
    }

    /// <summary>
    /// The VALUE wins over the sentence, which is the whole reason it exists — and the only test
    /// here that could not pass on the fallback alone.
    ///
    /// <para>The test above cannot tell them apart: the summary it uses contains the reason too, so
    /// parsing the sentence produces the same answer and the test would stay green with the payload
    /// removed. That is a test green for a reason other than the one in its name, which is the thing
    /// this project keeps catching. Here the two disagree on purpose.</para>
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

        var note = Assert.Single(RunReplayPlan.Segments(record)).Note;

        Assert.Contains("what actually stopped it", note);
        Assert.DoesNotContain("an older wording", note);
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

        Assert.Contains("unresolved tool call", Assert.Single(RunReplayPlan.Segments(record)).Note);
    }
}
