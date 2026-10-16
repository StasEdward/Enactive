namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Application.Projections;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// A step sent back or rejected by the engine's coverage check (StepCoverageGate) was seen by no reviewer, and nothing a
/// person reads says one did: not the step's status, not its card, not the log, not who is at work.
/// </summary>
public sealed class TheCoverageCheckIsNotAReviewTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary, string? payload)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent GateFailed(int step = 2)
        => Ev(EventKind.ReviewFailed, $"[{step}] FAIL (the engine's coverage check - no reviewer was asked): covered 2 of 12",
            WorkEventPayload.EngineCheckPayload(step));

    [Fact]
    public void The_status_says_the_engines_coverage_check_rejected_it()
    {
        var record = new StepRecord(StepOutcomeKind.ReviewRejected, OutcomeCause.CoverageGateRejected, "covered 2 of 12", ResultStanding.Rejected, null);

        Assert.Equal("Rejected by the engine's coverage check", ItemReport.Status(record));
        Assert.Equal(ResultStanding.Rejected, StepRecord.StandingOf(StepOutcomeKind.ReviewRejected, OutcomeCause.CoverageGateRejected, true, false));
    }

    [Fact]
    public void Its_payload_says_it_is_the_engines()
    {
        Assert.True(GateFailed().ByEngineCheck());
        Assert.Equal(2, GateFailed().StepNo());
        Assert.False(Ev(EventKind.ReviewFailed, "[2] FAIL (Step review): wrong", "{\"step\":2}").ByEngineCheck());
    }

    [Fact]
    public void The_card_says_the_engine_sent_it_back_not_a_review()
    {
        var feed = new RunFeed(null);
        feed.Apply(Ev(EventKind.StepStarted, "[2/2] review pages", "{\"step\":2}"));

        feed.Apply(GateFailed());

        var card = feed.Cards.Last();
        Assert.Equal("The engine's coverage check sent it back…", card.Activity);
    }

    [Fact]
    public void Nobody_is_shown_reviewing_it()
    {
        var activity = new RunActivity();
        activity.Apply(Ev(EventKind.StepStarted, "step 2", "{\"step\":2}"));

        activity.Apply(GateFailed());

        Assert.Equal(AgentKind.Coder, activity.Agent);
        Assert.StartsWith("[2] FAIL (the engine's coverage check", activity.CurrentAction, StringComparison.Ordinal);
    }

    // ── how the step ended, and the run ──────────────────────────────────────────────────

    [Fact]
    public void The_end_card_says_the_engines_coverage_check_rejected_it()
    {
        Assert.Equal("Rejected by the engine's coverage check — covered 2 of 12",
            RunOutcomeWords.StepActivity(StepOutcomeKind.ReviewRejected, "covered 2 of 12", byEngineCheck: true));
        Assert.Equal("Rejected by the reviewer — wrong", RunOutcomeWords.StepActivity(StepOutcomeKind.ReviewRejected, "wrong"));

        var feed = new RunFeed(null);
        feed.Apply(Ev(EventKind.StepStarted, "[1/2] find pages", "{\"step\":1}"));
        feed.Apply(Ev(EventKind.StepCompleted, "[1/2] find pages — REVIEW REJECTED: wrong",
            WorkEventPayload.StepPayload(1, StepOutcomeKind.ReviewRejected, "wrong")));
        feed.Apply(Ev(EventKind.StepStarted, "[2/2] review pages", "{\"step\":2}"));
        feed.Apply(Ev(EventKind.StepCompleted, "[2/2] review pages — REJECTED BY THE ENGINE'S COVERAGE CHECK: covered 2 of 12",
            WorkEventPayload.StepPayload(2, StepOutcomeKind.ReviewRejected, "covered 2 of 12", engineCheck: true)));

        // A payload without the flag - every other rejection, and every one recorded before - reads as before.
        Assert.Equal("Rejected by the reviewer — wrong", feed.Cards[^2].Activity);
        Assert.Equal("Rejected by the engine's coverage check — covered 2 of 12", feed.Cards[^1].Activity);
    }

    [Fact]
    public void The_run_counts_the_engines_rejections_apart_from_the_reviewers()
    {
        Assert.Equal("1 step(s) rejected by the reviewer; 1 step(s) rejected by the engine's coverage check",
            RunOutcomeWords.Explain([StepOutcomeKind.ReviewRejected, StepOutcomeKind.ReviewRejected], [], cycle: false, rejectedByEngineCheck: 1));
        Assert.Equal("1 step(s) rejected by the reviewer",
            RunOutcomeWords.Explain([StepOutcomeKind.ReviewRejected], [], cycle: false));

        var before = RunOutcomeDecision.BeforeChecks(new RunWork(
        [
            new SettledStep(1, "find pages", StepOutcomeKind.Succeeded),
            new SettledStep(2, "review pages", StepOutcomeKind.ReviewRejected, "covered 2 of 12", Cause: OutcomeCause.CoverageGateRejected)
        ], Resumable: false));
        Assert.Equal("covered 2 of 12 (1 step(s) rejected by the engine's coverage check)", before.Reason);

        Assert.Equal("rejected by the engine's coverage check", RunOutcomeDecision.Word(StepOutcomeKind.ReviewRejected, OutcomeCause.CoverageGateRejected));
        Assert.Equal("review rejected", RunOutcomeDecision.Word(StepOutcomeKind.ReviewRejected));
    }

    private sealed class Sink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];
        public void Log(LogEntry entry) => Entries.Add(entry);
    }

    private static async IAsyncEnumerable<WorkEvent> Of(params WorkEvent[] events)
    {
        foreach (var ev in events) yield return ev;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_log_has_it_from_the_orchestrator_not_the_reviewer()
    {
        var sink = new Sink();
        await foreach (var _ in Of(GateFailed(), Ev(EventKind.ReviewFailed, "[2] FAIL (Step review): wrong", "{\"step\":2}")).TeeToLog(sink)) { }

        Assert.Equal(LogSource.Orchestrator, sink.Entries[0].Source);
        Assert.Equal(LogLevel.Warn, sink.Entries[0].Level);
        Assert.Equal(LogSource.Reviewer, sink.Entries[1].Source);
    }
}
